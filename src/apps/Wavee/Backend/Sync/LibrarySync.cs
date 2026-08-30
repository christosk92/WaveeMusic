using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Channels = System.Threading.Channels;   // alias: 'Channel' alone collides with Wavee.Backend.Channel (transport enum)
using Wavee.Backend.Collections;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Col = Wavee.Protocol.Collection;

namespace Wavee.Backend.Sync;

// ── The single library-sync writer loop (§0 tenet 1, §2.2) ───────────────────────────────────────────────────────────
// One serialized consumer owns every network-sourced library-state write (rootlist, memberships, collection sets, revision
// bumps) + the mutation-outbox drain. The DealerRouter/Seam/on-open path/boot only ENQUEUE typed commands; nothing else
// races a store write for the same entity. Optimistic user writes stay inline (UI-frame latency) — their replay/reconcile
// runs here via DrainWrites. Placement in Backend/ keeps it unit-testable against StubTransport.PushEvent + crafted protos.
//
// Phase-1/2/3 scope (docs/library-sync-implementation-plan.md §12): PlaylistRevalidate = full FetchPlaylistAsync (the /diff
// upgrade is Phase 5 — one call-site swap); CollectionPush carries the WIRE set + raw payload — a parseable PubSubUpdate is
// direct-applied on the loop (echo-dropped via the ring, else the items folded through the pending shield, zero round-trip),
// and only an unparseable/empty/zero-item payload falls back to the 250ms-settled delta fetch (wire → logical fan-out).
// ReconnectResync (§6.2): the ordered convergence pass on a drop→Online transition — drain first (local intent wins),
// then rootlist, then token-gated per-set deltas, then /diff for the open + dirty RESIDENT playlists only (anti-herd
// preserved: cold-dirty playlists stay lazy). Rate-limited to one pass per 30s so a flapping network can't storm.
// ReconcileCollections: the drift proof the delta model cannot give itself — a shadow walk of every WIRE set compared
// against the local sets (CollectionFetcher.ReconcileWireSetAsync), repaged only on verified drift. Runs after a
// reconnect pass and every ReconcileInterval; never twice inside ReconcileMinGap.
//
// Collection sets are walked, tokened and reconciled per WIRE set (CollectionSets.WireSets — four, not the five logical
// sets): "collection" is ONE walk for liked + albums, so the two halves of the same mixed snapshot can no longer sweep
// each other, and its token is stored once under the wire key.

public enum SyncKind : byte
{
    InitialHydrate, RootlistPush, PlaylistPush, CollectionPush, OpenPlaylist, PlaylistRevalidate, DrainWrites, ReconnectResync,
    ApplyPlaylistSignal, HydratePlaylist, PermissionPush, SeedPermission,
    /// <summary>The collection drift check over every wire set. <c>Uri</c> carries the trigger ("reconnect"/"periodic").</summary>
    ReconcileCollections,
}

/// <summary>A queued command for the sync loop. A readonly record struct through the unbounded channel (no boxing).
/// <see cref="Done"/> completes when the command's handler finishes (OpenPlaylist awaits it; tests use it as a barrier).</summary>
public readonly record struct SyncCommand(
    SyncKind Kind,
    string Uri = "",                                  // playlist uri / set id
    byte[]? ParentRev = null,
    byte[]? NewRev = null,
    IReadOnlyList<PlaylistOp>? Ops = null,
    byte[]? Payload = null,                           // raw collection-push payload (§2.3 — passed through, unused in Phase 1)
    TaskCompletionSource? Done = null,
    string? OptionIdentifier = null,
    int Attempt = 0,
    PlaylistPermissionPush? Permission = null);   // SyncKind.PermissionPush payload (hm://playlist-permission/…/state)

public sealed class LibrarySync : IPlaylistTuningSource, IAsyncDisposable
{
    const int SettleMs = 250;                                                                 // dealer-burst settle (§2.2)
    static readonly TimeSpan OpenRevalidateWindow = TimeSpan.FromMinutes(5);                  // on-open SWR window (§2.2)
    static readonly TimeSpan SetRetryDelay = TimeSpan.FromSeconds(30);                        // per-set hydrate retry (§8.2)

    readonly IStore _store;
    readonly PlaylistFetcher _playlists;
    readonly CollectionFetcher _collections;
    readonly MutationEngine _mutations;
    readonly ITransport _mutationTransport;
    // I4 — the uris a /changes response could not fold in place. Shared instance with OpRebaseStrategy; drained (and
    // revalidated) right after every outbox drain. Required, never optional: an unwired queue silently loses convergence.
    readonly PlaylistResyncQueue _resync;
    // The permission read for the on-open owner seed (§P1.3). Built from the SAME transport the drain uses, so it is
    // never null and never "optional" — a session without a live transport simply gets the stub's answer.
    readonly PlaylistPermissionClient _permissions;
    readonly CollectionEchoRing? _echoRing;   // §7.1 — drop our own accepted-write echoes before any store work
    readonly PlaylistSignalsClient? _signals;
    readonly Func<SessionContext> _ctx;
    readonly Func<string> _username;
    readonly WaveeLogger _log;
    readonly CancellationToken _ct;
    readonly Channels.Channel<SyncCommand> _queue = Channels.Channel.CreateUnbounded<SyncCommand>(new Channels.UnboundedChannelOptions { SingleReader = true });
    readonly Task _consumer;

    readonly object _gate = new();
    readonly HashSet<string> _dirtyPlaylists = new(StringComparer.Ordinal);            // pushed-while-cold → revalidate on open
    readonly HashSet<string> _attrHealForced = new(StringComparer.Ordinal);           // uris force-refetched once for attr-less rows (loop guard)
    readonly Dictionary<string, DateTime> _lastRevalidatedAt = new(StringComparer.Ordinal);
    readonly Dictionary<string, TaskCompletionSource> _openInFlight = new(StringComparer.Ordinal);  // per-uri open dedup
    readonly HashSet<string> _pendingSets = new(StringComparer.Ordinal);              // collection-push settle coalescing
    readonly HashSet<string> _loggedUnknownSets = new(StringComparer.Ordinal);        // unknown wire sets logged at most once
    string? _openUri;                                                                 // the on-screen playlist (SetOpenContext)
    bool _openPermissionSeeded;                                                       // P1.3 — one permission GET per open context
    int _consecutiveDrainFailures;
    bool _drainReenqueueScheduled;
    DateTime _lastResyncAt = DateTime.MinValue;                                       // §6.2 rate limit (one pass per window)
    DateTime _lastReconcileAt = DateTime.MinValue;                                    // ReconcileMinGap clock
    bool _reconcileScheduled;                                                         // one periodic timer chain, never two

    /// <summary>The §6.2 resync rate-limit window (default 30s). Public only so tests can collapse it; production never sets it.</summary>
    public TimeSpan ResyncWindow = TimeSpan.FromSeconds(30);
    /// <summary>Minimum gap between two collection reconcile passes (default 5 min): a reconnect burst right after a
    /// periodic pass must not walk every wire set again. Public only so tests can collapse it.</summary>
    public TimeSpan ReconcileMinGap = TimeSpan.FromMinutes(5);
    /// <summary>The periodic reconcile cadence (default 6h) — armed after InitialHydrate and re-armed after every pass.
    /// A shadow walk is one uri-only page per 300 members per wire set, so this is cheap even for a 10k library.
    /// Public only so tests can collapse it.</summary>
    public TimeSpan ReconcileInterval = TimeSpan.FromHours(6);

    // Counters (§11) — test + probe visibility. Interlocked-bumped.
    public int PushApplied, PushMarkedDirty, PushDirectApplied, EchoDropped, RootlistApplied, SetFetches;
    public int DiffApplied, DiffUpToDate, DiffFellBack;   // §2.6 revalidation outcomes (Applied / 304-or-up-to-date / full-fetch fallback)
    public int ReconnectResyncs, ReconnectResyncsRateLimited;                         // §6.2
    /// <summary>Collection reconcile passes run / wire sets found drifted (repaged or skipped-unverified) / passes
    /// dropped by <see cref="ReconcileMinGap"/>.</summary>
    public int ReconcilePasses, ReconcileDrifts, ReconcilesSkipped;
    /// <summary>I6 — rootlist heads dropped because the stored revision already IS that head (our own write's echo).</summary>
    public int RootlistEchoDropped;
    /// <summary>I1 — a persisted rootlist revision that was not 24 bytes and had to be cleared at start.</summary>
    public int RootlistRevisionsHealed;
    /// <summary>P1 — permission pushes folded into a resident header / dropped because the header was cold.</summary>
    public int PermissionPushesApplied, PermissionPushesIgnored, PermissionSeeds;
    /// <summary>P1 — remote deletes (deleted_by_owner) applied.</summary>
    public int Tombstones;
    /// <summary>I3(a) — pushes that only marked dirty because a local intent for that uri was still pending.</summary>
    public int PushDeferredPending;
    public int SignalApplies;

    public LibrarySync(IStore store, PlaylistFetcher playlists, CollectionFetcher collections, MutationEngine mutations,
        PlaylistResyncQueue resync,
        ITransport mutationTransport, Func<SessionContext> ctx, Func<string> username, WaveeLogger log, CancellationToken ct,
        CollectionEchoRing? echoRing = null, PlaylistSignalsClient? signals = null)
    {
        _store = store;
        _playlists = playlists;
        _collections = collections;
        _mutations = mutations;
        _resync = resync;
        _mutationTransport = mutationTransport;
        _permissions = new PlaylistPermissionClient(mutationTransport);
        _echoRing = echoRing;
        _signals = signals;
        _ctx = ctx;
        _username = username;
        _log = log;
        _ct = ct;
        _consumer = Task.Run(ConsumeAsync);
    }

    // ── public surface ──────────────────────────────────────────────────────────────────────────────────────────────
    // CollectionPush routing (§2.2): a payload that will DIRECT-APPLY or ECHO-DROP (a parseable PubSubUpdate with items, or
    // one whose client_update_id is in the echo ring) bypasses the settle entirely — it is O(items), no network, so it runs
    // immediately on the loop. Everything else (unparseable/empty/zero-item) arms the 250ms settle OUT of the consumer: a
    // settling set does NOT stall the loop, and a second push for the same wire set folds while the first is still settling.
    public void Enqueue(in SyncCommand cmd)
    {
        if (cmd.Kind == SyncKind.CollectionPush)
        {
            if (ShouldDirectApply(cmd.Payload)) _queue.Writer.TryWrite(cmd);   // immediate — no settle, applied on the loop
            else ScheduleCollectionSettle(cmd);                               // fetch path — settle + wire→logical fan-out
            return;
        }
        _queue.Writer.TryWrite(cmd);
    }

    // Fold + settle the collection burst off the consumer thread. First push for a set arms the settle (and its payload —
    // §2.3 — is the one Phase 3 parses); subsequent pushes within the window are dropped (already pending). IsSetSyncing is
    // true from this add until the follow-up handler's fetch completes and removes the set.
    void ScheduleCollectionSettle(in SyncCommand cmd)
    {
        var set = cmd.Uri;
        if (set.Length == 0) { cmd.Done?.TrySetResult(); return; }
        lock (_gate) { if (!_pendingSets.Add(set)) { cmd.Done?.TrySetResult(); return; } }   // folded into the in-flight settle
        var payload = cmd.Payload;
        var done = cmd.Done;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(SettleMs, _ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { lock (_gate) _pendingSets.Remove(set); done?.TrySetResult(); return; }
            // Write the settled follow-up directly to the channel (bypassing Enqueue's interception); the handler fetches
            // immediately (no further delay) and clears _pendingSets in its finally. If the writer is closed (dispose),
            // undo the pending mark and release any barrier.
            if (!_queue.Writer.TryWrite(new SyncCommand(SyncKind.CollectionPush, set, Payload: payload, Done: done)))
            { lock (_gate) _pendingSets.Remove(set); done?.TrySetResult(); }
        });
    }

    /// <summary>The DetailPage mount effect sets the on-screen playlist so a push for it revalidates eagerly (§2.2 gate 3).
    /// <para>Opening an OWNED playlist also seeds its base permission into the store header (P1.3): the page reads
    /// <c>IsPublic</c>/<c>BasePermissionRevision</c>/<c>Capabilities.IsCollaborative</c> from the store and never issues
    /// its own permission GET, and a later <c>permission/state</c> dealer push converges the same fields with no GET.</para></summary>
    public void SetOpenContext(string? uri)
    {
        lock (_gate) { _openUri = uri; _openPermissionSeeded = false; }
        TrySeedPermissionForOpen(uri);
    }

    /// <summary>Clear the visible playlist only when this caller still owns the slot. A parked page's delayed cleanup
    /// must not clobber the context installed by the page that replaced it.</summary>
    public void ClearOpenContext(string uri)
    {
        lock (_gate)
        {
            if (_openUri != uri) return;
            _openUri = null;
            _openPermissionSeeded = false;
        }
    }

    /// <summary>Seed the open playlist's base permission ONCE per open context, as soon as an owned header exists.
    ///
    /// <para>The gate cannot live in <see cref="SetOpenContext"/> alone. <see cref="IsOwned"/> reads the STORE header,
    /// and on a cold deep link (a shared link, a restart onto a playlist page) the page's mount effect calls
    /// SetOpenContext before the header has landed — the open fetch is still in flight — so the owner check said "not
    /// mine", nothing was enqueued, and nothing ever re-asked. The visible symptom was a private playlist the user owns
    /// rendering with no Private eyebrow until they navigated away and back. So the header-landing paths on this loop
    /// (<see cref="AfterNetworkSnapshot"/>, and the header heal in <see cref="OpenPlaylistCoreAsync"/>) re-evaluate it.</para>
    ///
    /// <para>ONCE per open context, tracked by <c>_openPermissionSeeded</c>: every revalidate of the open playlist runs
    /// through AfterNetworkSnapshot, and a permission GET per /diff is exactly the herd the on-open seed was designed to
    /// replace. A dealer <c>permission/state</c> push converges the fields afterwards for free.</para></summary>
    void TrySeedPermissionForOpen(string? uri)
    {
        if (uri is not { Length: > 0 }) return;
        // Cheap pre-check first, so the common case (already seeded; every later revalidate of the open playlist comes
        // through here) costs one lock and no store read at all.
        lock (_gate) if (_openUri != uri || _openPermissionSeeded) return;
        // The store read stays OUTSIDE the lock — this runs both from the UI thread (SetOpenContext) and from the sync
        // loop, and holding _gate across another component's lock is how ordering bugs are grown.
        if (!IsOwned(uri)) return;   // not ours (or no header yet) — a later header landing re-asks this question
        lock (_gate)
        {
            if (_openUri != uri || _openPermissionSeeded) return;   // re-check: the other caller may have won meanwhile
            _openPermissionSeeded = true;
        }
        Enqueue(new SyncCommand(SyncKind.SeedPermission, uri));
    }

    // Owner-only: the permission endpoints 403 for everyone else, and a non-owner's public/private state is not editable.
    bool IsOwned(string uri)
        => _store.GetPlaylist(uri) is { } header
           && (header.Capabilities.IsOwner || header.Capabilities.CanAdministratePermissions);

    /// <summary>Optional UI progress hook: is a full set fetch currently settling/running.</summary>
    public bool IsSetSyncing(string setId) { lock (_gate) return _pendingSets.Contains(setId); }

    /// <summary>On-open path (EnsureFetchedAsync): enqueue + await completion, DEDUPED per uri (a second open while one is
    /// in-flight awaits the same task). Empty membership → full fetch; else dirty/stale-gated revalidate.</summary>
    public Task OpenPlaylistAsync(string uri, CancellationToken ct)
    {
        TaskCompletionSource tcs;
        lock (_gate)
        {
            if (_openInFlight.TryGetValue(uri, out var existing))
                return ct.CanBeCanceled ? existing.Task.WaitAsync(ct) : existing.Task;
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _openInFlight[uri] = tcs;
        }
        Enqueue(new SyncCommand(SyncKind.OpenPlaylist, uri, Done: tcs));
        return ct.CanBeCanceled ? tcs.Task.WaitAsync(ct) : tcs.Task;
    }

    /// <summary>Enqueue a mutation-outbox drain on the single-writer loop and await that command's completion. User-facing
    /// playlist actions use this barrier so they never report a queued write as a confirmed server mutation.</summary>
    public Task DrainWritesAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new SyncCommand(SyncKind.DrainWrites, Done: tcs)))
            return Task.FromException(new InvalidOperationException("The library sync loop is not available."));
        return ct.CanBeCanceled ? tcs.Task.WaitAsync(ct) : tcs.Task;
    }

    /// <summary>Queues a server-advertised playlist tuning choice on the single-writer loop.</summary>
    public Task ApplyAsync(string playlistUri, string optionIdentifier, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new SyncCommand(
                SyncKind.ApplyPlaylistSignal,
                playlistUri,
                Done: tcs,
                OptionIdentifier: optionIdentifier)))
            return Task.FromException(new InvalidOperationException("The library sync loop is not available."));
        return ct.CanBeCanceled ? tcs.Task.WaitAsync(ct) : tcs.Task;
    }

    /// <summary>Test/probe barrier: a no-op that completes only after all previously-queued commands are processed
    /// (the channel is FIFO single-reader). A PlaylistRevalidate with an empty uri is the idle sentinel.</summary>
    public Task WaitForIdleAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new SyncCommand(SyncKind.PlaylistRevalidate, "", Done: tcs));
        return tcs.Task;
    }

    // ── the consumer loop ───────────────────────────────────────────────────────────────────────────────────────────
    async Task ConsumeAsync()
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_ct).ConfigureAwait(false))
                while (reader.TryRead(out var cmd))
                {
                    try { await Dispatch(cmd).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (_ct.IsCancellationRequested) { cmd.Done?.TrySetResult(); return; }
                    catch (Exception ex) { _log.Info("sync: " + cmd.Kind + " failed: " + ex.Message); }
                    finally { cmd.Done?.TrySetResult(); }
                }
        }
        catch (OperationCanceledException) { /* cancelled (logout) — fall through to complete stragglers */ }
        catch (Exception ex) { _log.Info("sync: loop crashed: " + ex.Message); }
        finally
        {
            while (reader.TryRead(out var leftover)) leftover.Done?.TrySetResult();
            lock (_gate) { foreach (var t in _openInFlight.Values) t.TrySetResult(); _openInFlight.Clear(); }
        }
    }

    Task Dispatch(SyncCommand cmd) => cmd.Kind switch
    {
        SyncKind.InitialHydrate => InitialHydrateAsync(),
        SyncKind.RootlistPush => RootlistPushAsync(cmd.ParentRev, cmd.NewRev, cmd.Ops),
        SyncKind.PlaylistPush => PlaylistPushAsync(cmd.Uri, cmd.ParentRev, cmd.NewRev, cmd.Ops),
        SyncKind.CollectionPush => CollectionPushAsync(cmd.Uri, cmd.Payload),
        SyncKind.OpenPlaylist => OpenPlaylistHandlerAsync(cmd.Uri),
        SyncKind.PlaylistRevalidate => PlaylistRevalidateAsync(cmd.Uri),
        SyncKind.DrainWrites => DrainWritesAsync(),
        SyncKind.ReconnectResync => ReconnectResyncAsync(),
        SyncKind.ApplyPlaylistSignal => ApplyPlaylistSignalAsync(cmd),
        SyncKind.HydratePlaylist => HydratePlaylistAsync(cmd.Uri, cmd.Attempt),
        SyncKind.PermissionPush => PermissionPushAsync(cmd.Permission),
        SyncKind.SeedPermission => SeedPermissionAsync(cmd.Uri),
        SyncKind.ReconcileCollections => ReconcileCollectionsAsync(cmd.Uri.Length > 0 ? cmd.Uri : "periodic"),
        _ => Task.CompletedTask,
    };

    // ── handlers ────────────────────────────────────────────────────────────────────────────────────────────────────
    async Task InitialHydrateAsync()
    {
        // (0) I1 — heal a malformed persisted rootlist revision BEFORE anything reads it (the drain's rootlist ops
        // would otherwise POST against it, and the fold below would compare against it).
        HealRootlistRevision();

        // (1) drain the outbox first — local intent wins (§6.3).
        await DrainWritesAsync().ConfigureAwait(false);

        // (2) rootlist (full fetch — the /diff upgrade is a later phase) + the "playlists" saved-set fold, one bulk.
        int rootCount = 0;
        try
        {
            using (_store.BeginBulk())
            {
                await _playlists.FetchRootlistAsync(RootlistUri(), _ct).ConfigureAwait(false);
                FoldRootlistIntoSavedSet();
            }
            rootCount = _store.Rootlist().Count(e => e.Kind == 0);
        }
        catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.Info("sync: rootlist hydrate failed: " + ex.Message); }

        // (3) the 4 WIRE sets sequentially, per-set failures isolated (log + record); retry the failed ones once after 30s.
        //     Each walk is token-gated (delta) or a verified snapshot; the per-logical-set counts are what the user sees.
        var counts = new List<string>(CollectionSets.WireSets.Length + 1);
        var failed = new List<string>();
        foreach (var wireSet in CollectionSets.WireSets)
        {
            _ct.ThrowIfCancellationRequested();
            try
            {
                var outcome = await FetchWireSetAsync(wireSet).ConfigureAwait(false);
                foreach (var set in CollectionSets.LogicalSetsForWireSet(wireSet))
                    counts.Add(set + "=" + _store.SavedUris(set).Count + (outcome == CollectionFetchOutcome.Delta ? "" : " (" + outcome + ")"));
            }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { failed.Add(wireSet); _log.Info("sync: wire set '" + wireSet + "' hydrate failed: " + ex.Message); }
        }
        if (failed.Count > 0) ScheduleSetRetry(failed);

        // (4) summary, then arm the periodic drift check. Boot itself does not reconcile: a null-token wire set was just
        //     walked under the ledger, and a delta'd one is proven by the first periodic/reconnect pass instead of doubling
        //     every launch's collection traffic.
        _log.Info($"sync: initial hydrate — {rootCount} rootlist playlists; " + string.Join(", ", counts)
            + (failed.Count > 0 ? " (failed: " + string.Join(",", failed) + ", retry in 30s)" : ""));
        ScheduleReconcile(ReconcileInterval);
    }

    // The rootlist push gate tree (I1/I6). Ordered, total, and never able to store a non-24-byte head:
    //   (1) malformed head        → full GET (defensive: the router already drops these before they reach the loop)
    //   (2) stored == head        → the echo of our own write → drop
    //   (3) parent matches + ops  → apply in place, adopt the head
    //   (4) otherwise             → full GET (this is where every head-only push lands)
    // An empty-ops push NEVER calls SetRootlist: adopting a head we did not apply ops for would make the next real
    // ops-carrying push parent-match against rows that were never updated.
    async Task RootlistPushAsync(byte[]? parentRev, byte[]? newRev, IReadOnlyList<PlaylistOp>? ops)
    {
        var stored = _store.RootlistRevision();

        if (!PlaylistRevisions.IsWellFormed(newRev))
        {
            PlaylistMutationDiagnostics.RootlistBadRevision(newRev?.Length ?? 0, "rootlist-push");
            await FullRootlistFetchAsync("bad-revision").ConfigureAwait(false);
            return;
        }

        if (PlaylistRevisions.Equal(stored, newRev)) { Interlocked.Increment(ref RootlistEchoDropped); return; }

        if (ops is { Count: > 0 } && PlaylistRevisions.Equal(stored, parentRev))
        {
            var members = new List<PlaylistMember>();
            foreach (var e in _store.Rootlist()) members.Add(new PlaylistMember("", e.Uri, null, e.AddedAtMs));
            bool torn = false;
            try { PlaylistDiffApplier.Apply(members, ops); }
            catch (ArgumentOutOfRangeException) { torn = true; }   // torn apply → full fetch
            if (!torn)
            {
                using (_store.BeginBulk())
                {
                    _store.SetRootlist(RootlistTreeBuilder.EntriesFromUris(
                        members.Select(m => m.ItemUri), members.Select(m => m.AddedAt).ToArray()), newRev);
                    FoldRootlistIntoSavedSet();
                }
                Interlocked.Increment(ref RootlistApplied);
                PlaylistMutationDiagnostics.RootlistPushApplied(ops.Count);
                return;
            }
            await FullRootlistFetchAsync("torn-apply").ConfigureAwait(false);
            return;
        }

        await FullRootlistFetchAsync(ops is { Count: > 0 } ? "parent-mismatch" : "head-only").ConfigureAwait(false);
    }

    // full fetch fallback (rootlists are small; a full GET always converges).
    async Task FullRootlistFetchAsync(string reason)
    {
        PlaylistMutationDiagnostics.RootlistPushGet(reason);
        using (_store.BeginBulk())
        {
            await _playlists.FetchRootlistAsync(RootlistUri(), _ct).ConfigureAwait(false);
            FoldRootlistIntoSavedSet();
        }
    }

    // I1 self-heal. A rootlist revision persisted by an older build could be the URI bytes of a misparsed dealer push;
    // it is in SQLite meta, so it survives restarts and would keep failing every equality gate forever. Clear it before
    // anything reads it (before the drain, so a queued rootlist op cannot POST against it) — the hydrate's full GET
    // rewrites the meta row with the real head.
    void HealRootlistRevision()
    {
        var stored = _store.RootlistRevision();
        if (stored is null || PlaylistRevisions.IsWellFormed(stored)) return;
        _store.SetRootlist(_store.Rootlist(), null);
        Interlocked.Increment(ref RootlistRevisionsHealed);
        PlaylistMutationDiagnostics.RootlistRevisionHealed(stored.Length);
    }

    const string ResetSignalIdentifier = "session-control-reset";

    async Task ApplyPlaylistSignalAsync(SyncCommand cmd)
    {
        bool sent = false;
        long started = Environment.TickCount64;
        try
        {
            if (_signals is null) throw new InvalidOperationException("Playlist tuning is not available in this session.");
            if (cmd.Uri.Length == 0 || string.IsNullOrWhiteSpace(cmd.OptionIdentifier))
                throw new ArgumentException("A playlist and tuning option are required.");

            var revision = _store.PlaylistRevision(cmd.Uri);
            if (!PlaylistRevisions.IsWellFormed(revision))
                throw new InvalidOperationException("The playlist tuning revision is stale.");
            var tuning = _store.GetPlaylist(cmd.Uri)?.Tuning;
            if (tuning is null || !PlaylistRevisions.Equal(tuning.Revision, revision))
                throw new InvalidOperationException("The playlist tuning roster is stale.");

            PlaylistTuningOption? requested = null;
            for (int i = 0; i < tuning.Available.Count; i++)
                if (string.Equals(tuning.Available[i].Identifier, cmd.OptionIdentifier, StringComparison.Ordinal))
                { requested = tuning.Available[i]; break; }
            if (requested is null) throw new InvalidOperationException("That playlist tuning option is no longer available.");
            if (requested.Kind == PlaylistTuningOptionKind.Reset
                    ? tuning.SelectedIdentifier is null
                    : string.Equals(tuning.SelectedIdentifier, requested.Identifier, StringComparison.Ordinal))
                return;

            _log.Event(WaveeLogLevel.Info, "playlist.signal.apply.start", "Applying playlist tuning signal",
                fields:
                [
                    WaveeLogField.Of("playlist", cmd.Uri),
                    WaveeLogField.Of("signal", requested.Identifier),
                ]);
            sent = true;
            var snapshot = await _signals.ApplyAsync(cmd.Uri, revision, requested.Identifier, _ct).ConfigureAwait(false);
            _playlists.AdoptSnapshot(cmd.Uri, snapshot);
            AfterNetworkSnapshot(cmd.Uri);
            ClearDirty(cmd.Uri);
            MarkRevalidated(cmd.Uri);
            try
            {
                await _playlists.HydrateMembershipAsync(cmd.Uri, _ct).ConfigureAwait(false);
                _store.Bump(cmd.Uri);
            }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.Info("sync: playlist signal metadata hydration deferred: " + ex.Message);
                ScheduleHydrationRetry(cmd.Uri, 0);
            }
            Interlocked.Increment(ref SignalApplies);
            _log.Event(WaveeLogLevel.Info, "playlist.signal.apply.ok", "Playlist tuning signal applied",
                elapsedMs: Environment.TickCount64 - started,
                fields:
                [
                    WaveeLogField.Of("playlist", cmd.Uri),
                    WaveeLogField.Of("signal", requested.Identifier),
                    WaveeLogField.Of("tracks", snapshot.Length),
                ]);
        }
        catch (OperationCanceledException) when (_ct.IsCancellationRequested)
        {
            cmd.Done?.TrySetCanceled(_ct);
        }
        catch (Exception ex)
        {
            if (sent && await ReconcilePlaylistSignalAsync(cmd.Uri, cmd.OptionIdentifier!).ConfigureAwait(false))
            {
                _log.Event(WaveeLogLevel.Info, "playlist.signal.apply.reconciled",
                    "Playlist tuning signal reconciled after an ambiguous response",
                    elapsedMs: Environment.TickCount64 - started,
                    fields:
                    [
                        WaveeLogField.Of("playlist", cmd.Uri),
                        WaveeLogField.Of("signal", cmd.OptionIdentifier!),
                    ]);
                return;
            }
            _log.Event(WaveeLogLevel.Error, "playlist.signal.apply.failed", "Playlist tuning signal failed",
                elapsedMs: Environment.TickCount64 - started,
                ex: ex,
                fields:
                [
                    WaveeLogField.Of("playlist", cmd.Uri),
                    WaveeLogField.Of("signal", cmd.OptionIdentifier ?? ""),
                    WaveeLogField.Of("sent", sent),
                ]);
            cmd.Done?.TrySetException(ex);
        }
    }

    async Task<bool> ReconcilePlaylistSignalAsync(string uri, string optionIdentifier)
    {
        try { await _playlists.FetchPlaylistAsync(uri, _ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.Info("sync: playlist signal reconciliation fetch failed: " + ex.Message); }

        var tuning = _store.GetPlaylist(uri)?.Tuning;
        var revision = _store.PlaylistRevision(uri);
        if (tuning is null || !PlaylistRevisions.Equal(tuning.Revision, revision)) return false;
        return string.Equals(optionIdentifier, ResetSignalIdentifier, StringComparison.Ordinal)
            ? tuning.SelectedIdentifier is null
            : string.Equals(tuning.SelectedIdentifier, optionIdentifier, StringComparison.Ordinal);
    }

    async Task HydratePlaylistAsync(string uri, int attempt)
    {
        if (uri.Length == 0) return;
        try
        {
            await _playlists.HydrateMembershipAsync(uri, _ct).ConfigureAwait(false);
            _store.Bump(uri);
        }
        catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.Info("sync: playlist signal metadata hydration retry failed: " + ex.Message);
            if (attempt < 2) ScheduleHydrationRetry(uri, attempt + 1);
        }
    }

    void ScheduleHydrationRetry(string uri, int attempt)
    {
        int seconds = attempt switch { 0 => 2, 1 => 10, _ => 30 };
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), _ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            Enqueue(new SyncCommand(SyncKind.HydratePlaylist, uri, Attempt: attempt));
        });
    }

    async Task PlaylistPushAsync(string uri, byte[]? parentRev, byte[]? newRev, IReadOnlyList<PlaylistOp>? ops)
    {
        if (uri.Length == 0) return;
        var stored = _store.PlaylistRevision(uri);

        // gate 1 — echo of our own write (we advanced the revision from the /changes response): stored == newRev → drop.
        if (PlaylistRevisions.Equal(stored, newRev)) { Interlocked.Increment(ref EchoDropped); return; }

        // gate 2 — TOMBSTONE (a remote delete arrives as UPDATE_LIST new{deleted_by_owner=1}). Terminal and idempotent,
        // so it deliberately runs BEFORE the pending gate below: a playlist that no longer exists cannot be converged by
        // draining local intent — those ops dead-letter with Deleted on their next replay.
        if (CarriesTombstone(ops)) { ApplyTombstone(uri, "push"); return; }

        // gate 3 (I3a) — a LOCAL INTENT for this uri is still in flight. Never apply a push in place while our own ops
        // are unacked (the push describes a list that does not include them) and never spend a round-trip revalidating
        // into a state the drain is about to change: mark dirty, converge after the ack.
        if (_mutations.PendingFor(uri) > 0)
        {
            MarkDirty(uri);
            Interlocked.Increment(ref PushDeferredPending);
            Interlocked.Increment(ref PushMarkedDirty);
            return;
        }

        // gate 4 — NEW HEAD. A well-formed head with no usable parent and no ops ("the list rolled over, here is where
        // it is now"): the create echo (8-byte parent), an editorial/signal regeneration, and a foreign write whose ops
        // the dealer did not carry all take this shape. It is NOT a signal-regeneration marker — the open page just
        // revalidates (revision-gated /diff, which falls back to a full GET inside the fetcher) and a cold list goes
        // dirty so it revalidates lazily on open (anti-herd).
        if (PlaylistRevisions.IsWellFormed(newRev) && !PlaylistRevisions.IsWellFormed(parentRev) && (ops is null || ops.Count == 0))
        {
            if (IsOpen(uri)) await PlaylistRevalidateAsync(uri).ConfigureAwait(false);
            else { MarkDirty(uri); Interlocked.Increment(ref PushMarkedDirty); }
            return;
        }

        var membership = _store.Membership(uri);

        // gate 5 — resident + parent-rev match → apply ops in place (zero network), hydrate ONLY the added uris.
        if (membership.Count > 0 && ops is not null && PlaylistRevisions.Equal(stored, parentRev))
        {
            var list = new List<PlaylistMember>(membership);
            var before = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < membership.Count; i++) before.Add(membership[i].ItemUri);
            bool torn = false;
            try { PlaylistDiffApplier.Apply(list, ops); }
            catch (ArgumentOutOfRangeException) { torn = true; }
            if (!torn)
            {
                // I1 — adopt the head only when it is storable; otherwise keep the baseline we already trust.
                byte[]? adopted = stored;
                if (PlaylistRevisions.IsWellFormed(newRev)) adopted = newRev;
                else if (newRev is not null) PlaylistMutationDiagnostics.RootlistBadRevision(newRev.Length, "playlist-push");
                _store.SetMembership(uri, list, adopted);
                var added = new List<string>();
                for (int i = 0; i < list.Count; i++) { var u = list[i].ItemUri; if (!before.Contains(u)) added.Add(u); }
                if (added.Count > 0)
                {
                    try { await _playlists.HydrateUrisAsync(added, _ct).ConfigureAwait(false); _store.Bump(uri); }
                    catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { _log.Info("sync: hydrate added uris failed: " + ex.Message); }
                }
                if (ContainsUpdateList(ops))
                {
                    try { await _playlists.FetchPlaylistHeaderAsync(uri, _ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { _log.Info("sync: playlist header refresh failed: " + ex.Message); }
                }
                ClearDirty(uri);
                Interlocked.Increment(ref PushApplied);
                return;
            }
        }

        // gate 6 — open playlist revalidates eagerly; everything else marks dirty (anti-herd).
        if (IsOpen(uri)) await PlaylistRevalidateAsync(uri).ConfigureAwait(false);
        else { MarkDirty(uri); Interlocked.Increment(ref PushMarkedDirty); }
    }

    /// <summary>True when an op batch carries the remote-delete marker (<c>UPDATE_LIST new{deleted_by_owner=1}</c>).</summary>
    static bool CarriesTombstone(IReadOnlyList<PlaylistOp>? ops)
    {
        if (ops is null) return false;
        for (int i = 0; i < ops.Count; i++)
            if (ops[i].Kind == PlaylistOpKind.UpdateList && ops[i].ListPatch is { DeletedByOwner: true }) return true;
        return false;
    }

    /// <summary>The owner deleted this playlist elsewhere. ONE bulk write evicts every trace of it from the library
    /// projections — rootlist row, saved pill, membership — and latches <c>DeletedByOwner</c> on the header so an open
    /// page can render its "this playlist was deleted" notice instead of an empty skeleton. Idempotent: the dealer sends
    /// the tombstone on the playlist topic AND (via a rootlist head) as a rootlist change, and a full GET/diff whose
    /// header carries the flag lands here too.
    /// <para>The rootlist edit is revision-PRESERVING (the 1-arg <c>SetRootlist</c>): the delete's own rootlist head
    /// arrives separately, and adopting a head we did not apply ops for would break the next parent-match.</para></summary>
    public void ApplyTombstone(string uri, string source)
    {
        if (uri.Length == 0) return;
        using (_store.BeginBulk())
        {
            if (RootlistOps.RemovePlaylistEntry(_store.Rootlist(), uri) is { } trimmed) _store.SetRootlist(trimmed);
            _store.SetSaved("playlists", uri, false, SyncState.Confirmed);
            _store.SetMembership(uri, Array.Empty<PlaylistMember>(), null);
            if (_store.GetPlaylist(uri) is { } header) _store.UpsertPlaylist(header with { DeletedByOwner = true });
            _store.Bump(uri);
            _store.Bump("rootlist", CollectionKind.Playlists);
        }
        ClearDirty(uri);
        MarkRevalidated(uri);
        Interlocked.Increment(ref Tombstones);
        PlaylistMutationDiagnostics.PlaylistTombstoned(uri, source);
    }

    // hm://playlist-permission/…/permission/state — the authoritative public/private/collaborative state, applied with
    // ZERO network. A COLD header is deliberately ignored rather than fetched: the state is seeded on open (SeedPermission)
    // and a permission GET per push for a playlist nobody is looking at is pure herd.
    Task PermissionPushAsync(PlaylistPermissionPush? push)
    {
        if (push is null) return Task.CompletedTask;
        if (_store.GetPlaylist(push.Uri) is not { } header)
        {
            Interlocked.Increment(ref PermissionPushesIgnored);
            PlaylistMutationDiagnostics.PermissionPushIgnored(push.Uri, "cold-header");
            return Task.CompletedTask;
        }
        _store.UpsertPlaylist(header with
        {
            IsPublic = push.Level != PlaylistPermissionLevel.Blocked,
            BasePermissionRevision = push.RevisionHex,
            Capabilities = header.Capabilities with { IsCollaborative = push.IsCollaborative },
        });
        _store.Bump(push.Uri);
        Interlocked.Increment(ref PermissionPushesApplied);
        PlaylistMutationDiagnostics.PermissionPushApplied(push.Uri, push.Level, push.IsCollaborative);
        return Task.CompletedTask;
    }

    // On-open owner seed (P1.3): the ONE place a permission GET happens. The detail page reads the answer off the store
    // header, so it never issues its own GET and a later permission/state push converges the same fields for free.
    async Task SeedPermissionAsync(string uri)
    {
        if (uri.Length == 0 || !IsOwned(uri)) return;
        PlaylistBasePermission? perm;
        try { perm = await _permissions.GetBasePermissionAsync(uri, _ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.Info("sync: permission seed failed for " + uri + ": " + ex.Message); return; }
        if (perm is not { } p) return;
        if (_store.GetPlaylist(uri) is not { } header) return;
        _store.UpsertPlaylist(header with { IsPublic = p.IsPublic, BasePermissionRevision = p.Revision });
        _store.Bump(uri);
        Interlocked.Increment(ref PermissionSeeds);
    }

    /// <summary>I3(b)/I4 — the SINGLE membership-replace chokepoint on the sync loop. A network snapshot (full GET,
    /// <c>/diff</c> contents, a folded <c>sync_result</c>) lands here, the revision is I1-gated on the way in, and the
    /// still-pending local ops are re-applied on top so an unacked edit never visibly reverts mid-drain.</summary>
    public void AdoptSnapshot(string uri, IReadOnlyList<PlaylistMember> members, byte[]? revision)
    {
        if (uri.Length == 0) return;
        _mutations.AdoptSnapshot(uri, members, revision);
        _store.Bump(uri);
    }

    // CollectionPush handler. `wireSet` is the WIRE set as it comes off the dealer topic ("collection"/"artist"/…). A
    // parseable PubSubUpdate is handled with zero round-trip: an echo (cuid in the ring) is dropped, else the items are
    // folded straight into the store through the pending shield (§2.2 E). Only an unparseable/empty/zero-item payload falls
    // back to the delta fetch — ONE walk of the wire set, fanned out to its logical set(s) inside the fetcher (§2.2). A
    // direct-apply command bypassed the settle so it was never in _pendingSets; a fetch command was, and its finally clears it.
    async Task CollectionPushAsync(string wireSet, byte[]? payload)
    {
        // Only the SETTLE follow-up owns the _pendingSets mark (a direct-apply command bypassed the settle and never added
        // it — clearing it here would prematurely free a concurrent settle window). Enqueue routed non-direct payloads here.
        bool fromSettle = !ShouldDirectApply(payload);
        try
        {
            if (wireSet.Length == 0) return;

            if (TryParsePush(payload, out var upd))
            {
                var cuid = upd.ClientUpdateId;
                if (cuid.Length > 0 && (_echoRing?.Contains(cuid) ?? false)) { Interlocked.Increment(ref EchoDropped); return; }
                if (upd.Items.Count > 0) { await DirectApplyPushAsync(wireSet, upd).ConfigureAwait(false); return; }
                // parsed but zero items → unknown change shape → fall through to the delta fetch.
            }

            if (CollectionSets.LogicalSetsForWireSet(wireSet).Count == 0) { LogUnknownWireSetOnce(wireSet); return; }
            await FetchWireSetAsync(wireSet).ConfigureAwait(false);
        }
        finally { if (fromSettle) lock (_gate) _pendingSets.Remove(wireSet); }
    }

    // §2.2 E — apply the pushed items directly (zero collection round-trip). Each item is attributed to a LOGICAL set via
    // its URI prefix within the wire set, shielded (§7.2) items are skipped, and the rest fold under ONE bulk. Added spotify
    // uris are hydrated (metadata) as on the playlist-push path. The sync token is deliberately NOT advanced — the next
    // delta re-delivers these items idempotently (the Phase-0 no-op elision makes that silent).
    async Task DirectApplyPushAsync(string wireSet, Col.PubSubUpdate upd)
    {
        var added = new List<string>();
        var firstAddedBySet = new Dictionary<string, string>(StringComparer.Ordinal);
        using (_store.BeginBulk())
        {
            foreach (var it in upd.Items)
            {
                var logical = CollectionSets.LogicalSetForItem(wireSet, it.Uri);
                if (logical is null) continue;                          // not attributable to a known logical set
                if (_mutations.HasPending(logical, it.Uri)) continue;   // §7.2 — a local intent shields this (set, uri)
                _store.SetSaved(logical, it.Uri, !it.IsRemoved, SyncState.Confirmed);
                if (!it.IsRemoved && EntityUri.Parse(it.Uri).Provider == EntityProviders.Spotify)
                {
                    added.Add(it.Uri);
                    if (!firstAddedBySet.ContainsKey(logical)) firstAddedBySet[logical] = it.Uri;
                }
            }
        }
        if (added.Count > 0)
        {
            try
            {
                await _playlists.HydrateUrisAsync(added, _ct).ConfigureAwait(false);
               
                foreach (var kv in firstAddedBySet)
                    if (KindForLogicalSet(kv.Key) is { } kind) _store.Bump(kv.Value, kind);
            }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.Info("sync: direct-apply hydrate failed: " + ex.Message); }
        }
        Interlocked.Increment(ref PushDirectApplied);
    }

    static bool ContainsUpdateList(IReadOnlyList<PlaylistOp> ops)
    {
        for (int i = 0; i < ops.Count; i++)
            if (ops[i].Kind == PlaylistOpKind.UpdateList) return true;
        return false;
    }

    static CollectionKind? KindForLogicalSet(string setId) => setId switch
    {
        "albums" => CollectionKind.Albums,
        "artists" => CollectionKind.Artists,
        "shows" or "episodes" => CollectionKind.Shows,
        "playlists" => CollectionKind.Playlists,
        "liked" => CollectionKind.Liked,
        _ => null,
    };

    // A payload direct-applies (bypassing the settle) iff it parses to a PubSubUpdate that carries items OR is an echo of one
    // of our accepted writes (a cuid in the ring). Parsing is pure + off-loop-safe; the handler re-parses to do the work.
    bool ShouldDirectApply(byte[]? payload)
    {
        if (!TryParsePush(payload, out var upd)) return false;
        if (upd.Items.Count > 0) return true;
        return upd.ClientUpdateId.Length > 0 && (_echoRing?.Contains(upd.ClientUpdateId) ?? false);
    }

    static bool TryParsePush(byte[]? payload, out Col.PubSubUpdate update)
    {
        update = null!;
        if (payload is null || payload.Length == 0) return false;
        try { update = Col.PubSubUpdate.Parser.ParseFrom(payload); return true; }
        catch { return false; }
    }

    void LogUnknownWireSetOnce(string wireSet)
    {
        bool first; lock (_gate) first = _loggedUnknownSets.Add(wireSet);
        if (first) _log.Info("sync: ignoring collection push for unknown wire set '" + wireSet + "'");
    }

    async Task OpenPlaylistHandlerAsync(string uri)
    {
        try
        {
            if (uri.Length == 0) return;
            await OpenPlaylistCoreAsync(uri).ConfigureAwait(false);
        }
        finally { lock (_gate) _openInFlight.Remove(uri); }
    }

    async Task OpenPlaylistCoreAsync(string uri)
    {
        var members = _store.Membership(uri);
        if (members.Count == 0)
        {
            await _playlists.FetchPlaylistAsync(uri, _ct).ConfigureAwait(false);   // first open — the skeleton path
            MarkRevalidated(uri); ClearDirty(uri);
            AfterNetworkSnapshot(uri);
            return;
        }
        // Attribute-aware heal gate. Membership can be resident yet attribute-less — every row has no added_at and no
        // added_by, so the Date-added / Added-by columns render blank forever: the /diff revalidate path (the only path
        // a NON-empty baseline takes) never re-reads item attributes for existing rows, so once a playlist was cached
        // without Item.attributes it stays that way. Treat that as still-cold and run the full, attribute-bearing
        // FetchPlaylistAsync instead — the same spirit as HydrationLevels.Of(Album) ("an unnamed track ⇒ not Open yet").
        // ALSO heals historically-poisoned caches written before this gate existed (recovery is lazy, per-open — no
        // SQLite migration). Force at most ONCE per session (_attrHealForced): a playlist whose server data genuinely
        // carries no attributes stays attribute-less after the fetch, and this guard stops it re-forcing a full GET on
        // every open — it falls through to the normal dirty/stale /diff path from the second open on.
        if (IsAttributeLess(members) && TryMarkAttrHealForced(uri))
        {
            await _playlists.FetchPlaylistAsync(uri, _ct).ConfigureAwait(false);
            MarkRevalidated(uri); ClearDirty(uri);
            AfterNetworkSnapshot(uri);
            return;
        }
        // Heal headers stripped or capability-stale after a partial LIST_METADATA_V2 upsert (membership stayed resident).
        var header = _store.GetPlaylist(uri);
        if (header is not null && (header.Capabilities == default
            || (header.Capabilities.CanEditMetadata && !header.Capabilities.CanAdministratePermissions)))
        {
            try { await _playlists.FetchPlaylistHeaderAsync(uri, _ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
            catch { }
            TrySeedPermissionForOpen(uri);   // the heal is a header LANDING too (P1.3, cold deep link)
        }
        bool dirty = IsDirty(uri);
        bool stale = !TryGetLastRevalidated(uri, out var last) || (DateTime.UtcNow - last) > OpenRevalidateWindow;
        // A ROLLING-IDENTITY playlist (a daylist and its future siblings) can roll to a wholly new edition well inside
        // this 5-minute window — every entry into this method is itself an "open" (OpenPlaylistAsync/Revalidate are
        // the only two callers), so there is no separate "is this a real user gesture" question to ask; the window
        // just must not veto one for a container whose identity moves on its own clock (cause 3 of the stale-daylist
        // defect — see PlaylistSnapshotFacts.IsRollingIdentity).
        bool rolling = PlaylistSnapshotFacts.IsRollingIdentity(header?.Format, header?.DaylistExpiresAtMs ?? 0);
        if (dirty || stale || rolling) await PlaylistRevalidateAsync(uri).ConfigureAwait(false);
    }

    // Revision-gated /diff (§2.6, fixes RC5): an unchanged playlist costs one up-to-date round-trip (usually a 304); a
    // changed one applies only the server's ops; every degenerate case (no baseline, stale rev/509, torn apply, bad body)
    // falls back to a full fetch inside the fetcher — all outcomes converge and mark the playlist fresh.
    async Task PlaylistRevalidateAsync(string uri)
    {
        if (uri.Length == 0) return;   // the WaitForIdleAsync idle barrier
        var outcome = await _playlists.FetchPlaylistDiffAsync(uri, _ct).ConfigureAwait(false);
        switch (outcome)
        {
            case DiffOutcome.Applied: Interlocked.Increment(ref DiffApplied); break;
            case DiffOutcome.UpToDate: Interlocked.Increment(ref DiffUpToDate); break;
            default: Interlocked.Increment(ref DiffFellBack); break;
        }
        MarkRevalidated(uri); ClearDirty(uri);
        AfterNetworkSnapshot(uri);
    }

    // Runs after EVERY network membership replace on the loop. Two jobs: (a) a header that came back carrying
    // deleted_by_owner is a tombstone, whatever path delivered it; (b) I3(b) — pending local ops are re-applied on top of
    // the fresh snapshot, so "add offline → reconnect → someone else edited → drain" never visibly reverts the add.
    void AfterNetworkSnapshot(string uri)
    {
        if (uri.Length == 0) return;
        if (_store.GetPlaylist(uri) is { DeletedByOwner: true }) { ApplyTombstone(uri, "header"); return; }
        int n = _mutations.ReapplyPending(uri);
        if (n > 0) PlaylistMutationDiagnostics.ReapplyPending(uri, n);
        // (c) P1.3 — a header just landed. If this is the OPEN playlist and it turns out to be ours, this is the first
        // moment the owner check can succeed on a cold deep link (see TrySeedPermissionForOpen). No-op otherwise, and
        // at most once per open context.
        TrySeedPermissionForOpen(uri);
    }

    async Task DrainWritesAsync()
    {
        lock (_gate) _drainReenqueueScheduled = false;   // this run consumes any scheduled re-enqueue
        await _mutations.Drain(_mutationTransport, _ctx(), _ct).ConfigureAwait(false);

        // I4 — a /changes response that reported multiple_heads / changes_require_resync / a torn sync_result did NOT
        // advance the stored revision; it dropped the uri here instead. Converge it now, on the single writer, with a
        // revision-gated /diff (which falls back to a full GET inside the fetcher).
        foreach (var uri in _resync.TakeAll())
        {
            MarkDirty(uri);
            try { await PlaylistRevalidateAsync(uri).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.Info("sync: post-drain resync of '" + uri + "' failed: " + ex.Message); }
        }

        if (_mutations.Pending > 0)
        {
            int fails;
            lock (_gate) fails = _consecutiveDrainFailures++;
            ScheduleDrainReenqueue(TimeSpan.FromSeconds(Math.Min(60d, Math.Pow(2, fails))));   // §8.3 backoff
        }
        else lock (_gate) _consecutiveDrainFailures = 0;   // a drain that empties the outbox resets the backoff
    }

    // §6.2 — the ordered convergence pass after a drop→Online transition. Everything is revision/token-gated, so an
    // eventless reconnect costs a handful of near-free probes. Order matters: drain FIRST (local intent wins — a delta
    // running first could visually revert a not-yet-sent like), then rootlist, then per-set deltas, then /diff for the
    // open playlist + the dirty RESIDENT playlists only (cold-dirty stays lazy — the anti-herd contract). Rate-limited:
    // pushes queued during the gap were dropped by the dead socket, so this pass is the only recovery; a flapping network
    // coalesces to one pass per window.
    async Task ReconnectResyncAsync()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (now - _lastResyncAt < ResyncWindow) { ReconnectResyncsRateLimited++; return; }
            _lastResyncAt = now;
        }

        await DrainWritesAsync().ConfigureAwait(false);                                    // (1) local intent first

        try                                                                                // (2) rootlist + fold
        {
            using (_store.BeginBulk())
            {
                await _playlists.FetchRootlistAsync(RootlistUri(), _ct).ConfigureAwait(false);
                FoldRootlistIntoSavedSet();
            }
        }
        catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.Info("sync: reconnect rootlist failed: " + ex.Message); }

        foreach (var wireSet in CollectionSets.WireSets)                                   // (3) token-gated deltas
        {
            _ct.ThrowIfCancellationRequested();
            try { await FetchWireSetAsync(wireSet).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.Info("sync: reconnect wire set '" + wireSet + "' failed: " + ex.Message); }
        }

        List<string> targets;                                                              // (4) open + dirty RESIDENT
        lock (_gate)
        {
            targets = new List<string>(_dirtyPlaylists.Count + 1);
            if (_openUri is { Length: > 0 } open) targets.Add(open);
            foreach (var d in _dirtyPlaylists) if (!targets.Contains(d)) targets.Add(d);
        }
        foreach (var uri in targets)
        {
            _ct.ThrowIfCancellationRequested();
            if (_store.Membership(uri).Count == 0) continue;   // cold stays lazy (revalidates on open)
            try { await PlaylistRevalidateAsync(uri).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.Info("sync: reconnect playlist '" + uri + "' failed: " + ex.Message); }
        }

        Interlocked.Increment(ref ReconnectResyncs);
        _log.Info("sync: reconnect resync complete (" + targets.Count + " playlist revalidations)");
        // (5) the drift proof, queued behind this pass rather than inlined: pushes that died with the socket are exactly
        //     the changes a capped delta can miss, and the min-gap guard keeps a flapping network from walking twice.
        Enqueue(new SyncCommand(SyncKind.ReconcileCollections, "reconnect"));
    }

    // The collection drift check. Every wire set is shadow-walked and compared against the local sets; a verified drift
    // repages off that same walk. Per-set failures are isolated (the next pass tries again). Whatever happened, the
    // periodic chain is re-armed here — a chain that died on one exception would silently end all future verification.
    async Task ReconcileCollectionsAsync(string trigger)
    {
        bool skip;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            skip = now - _lastReconcileAt < ReconcileMinGap;
            if (!skip) _lastReconcileAt = now;
        }
        if (skip)
        {
            Interlocked.Increment(ref ReconcilesSkipped);
            _log.Event(WaveeLogLevel.Debug, "collection.reconcile.skip", "Collection reconcile skipped (inside the minimum gap)",
                fields: [WaveeLogField.Of("trigger", trigger)]);
            ScheduleReconcile(ReconcileInterval);
            return;
        }

        int drifts = 0;
        foreach (var wireSet in CollectionSets.WireSets)
        {
            _ct.ThrowIfCancellationRequested();
            try
            {
                if (await _collections.ReconcileWireSetAsync(wireSet, trigger, _ct).ConfigureAwait(false) != CollectionReconcileOutcome.NoDrift) drifts++;
            }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.Info("sync: reconcile of wire set '" + wireSet + "' failed: " + ex.Message); }
        }
        Interlocked.Add(ref ReconcileDrifts, drifts);
        Interlocked.Increment(ref ReconcilePasses);
        ScheduleReconcile(ReconcileInterval);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────
    async Task<CollectionFetchOutcome> FetchWireSetAsync(string wireSet)
    {
        var outcome = await _collections.FetchWireSetAsync(wireSet, _ct).ConfigureAwait(false);
        Interlocked.Increment(ref SetFetches);
        return outcome;
    }

    void FoldRootlistIntoSavedSet()   // §2.8 — must run inside the caller's BeginBulk
    {
        var next = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in _store.Rootlist())
            if (e.Kind == 0 && EntityUri.KindOf(e.Uri) == EntityKind.Playlist) next.Add(e.Uri);

        foreach (var uri in next)
            if (!_store.IsSaved("playlists", uri) && !_mutations.HasPending("playlists", uri))
                _store.SetSaved("playlists", uri, true, SyncState.Confirmed);

        var current = _store.SavedUris("playlists");
        for (int i = 0; i < current.Count; i++)
        {
            var uri = current[i];
            if (!next.Contains(uri) && !_mutations.HasPending("playlists", uri))   // Pending-shielded rows survive the fold
                _store.SetSaved("playlists", uri, false, SyncState.Confirmed);
        }
    }

    void ScheduleSetRetry(List<string> wireSets)
    {
        // Retry keys on the WIRE set (CollectionPush's contract) — the same unit the hydrate walked, so a re-push
        // re-fetches every logical set the wire set carries. Idempotent + token-gated ⇒ a retry is cheap.
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(SetRetryDelay, _ct).ConfigureAwait(false); } catch { return; }
            foreach (var w in wireSets) Enqueue(new SyncCommand(SyncKind.CollectionPush, w));   // one-shot retry (settle + fetch)
        });
    }

    // The periodic reconcile timer — same shape as ScheduleDrainReenqueue: at most one armed at a time, so a reconnect
    // pass re-arming while the boot timer is pending does not fork a second chain. The flag clears when the timer
    // fires; the pass it enqueues re-arms the next one.
    void ScheduleReconcile(TimeSpan delay)
    {
        lock (_gate) { if (_reconcileScheduled) return; _reconcileScheduled = true; }
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, _ct).ConfigureAwait(false); } catch { lock (_gate) _reconcileScheduled = false; return; }
            lock (_gate) _reconcileScheduled = false;
            Enqueue(new SyncCommand(SyncKind.ReconcileCollections, "periodic"));
        });
    }

    void ScheduleDrainReenqueue(TimeSpan delay)
    {
        lock (_gate) { if (_drainReenqueueScheduled) return; _drainReenqueueScheduled = true; }
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, _ct).ConfigureAwait(false); } catch { return; }
            Enqueue(new SyncCommand(SyncKind.DrainWrites));
        });
    }

    string RootlistUri() => "spotify:user:" + _username() + ":rootlist";

    // Every row lacks BOTH membership facts (added_at <= 0 AND no added_by) ⇒ the cached membership was recorded without
    // Item.attributes; the joined Date-added / Added-by columns can never populate from it. Called only when Count > 0.
    static bool IsAttributeLess(IReadOnlyList<PlaylistMember> members)
    {
        for (int i = 0; i < members.Count; i++)
            if (members[i].AddedAt > 0 || !string.IsNullOrEmpty(members[i].AddedBy)) return false;
        return true;
    }

    // Once-per-session single-flight for the attr-less heal fetch: returns true the FIRST time a uri is forced (and records
    // it), false thereafter — so a genuinely attribute-less server playlist never storms a full GET on every open.
    bool TryMarkAttrHealForced(string uri) { lock (_gate) return _attrHealForced.Add(uri); }

    bool IsOpen(string uri) { lock (_gate) return _openUri == uri; }
    void MarkDirty(string uri) { lock (_gate) _dirtyPlaylists.Add(uri); }
    void ClearDirty(string uri) { lock (_gate) _dirtyPlaylists.Remove(uri); }
    bool IsDirty(string uri) { lock (_gate) return _dirtyPlaylists.Contains(uri); }
    void MarkRevalidated(string uri) { lock (_gate) _lastRevalidatedAt[uri] = DateTime.UtcNow; }
    bool TryGetLastRevalidated(string uri, out DateTime t) { lock (_gate) return _lastRevalidatedAt.TryGetValue(uri, out t); }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try { await _consumer.ConfigureAwait(false); } catch { /* cancelled / already stopped */ }
    }
}
