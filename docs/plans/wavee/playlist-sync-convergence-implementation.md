# Playlist sync silent non-convergence — implementation plan

**Status:** planned, not started. **Scope:** `src/apps/Wavee/Backend/{Mutation.cs, Playlists/PlaylistResyncQueue.cs,
Playlists/PlaylistMutationDiagnostics.cs, Sync/LibrarySync.cs}`, `src/apps/Wavee/App/{Services.cs, LibraryBridge.cs}`,
`src/apps/Wavee/Features/Detail/PlaylistInlineEdit.cs`, `assets/loc/en-US.json`, `Wavee.Tests`. Two other defects
depend on this landing first: the sidebar Recents sort (`sidebar-recents-sort-implementation.md`) is independent, but
the "track added from another device not shown as now-playing in its own playlist" symptom is a downstream effect of the
foreign edit never being applied through the sync gate — fixing convergence fixes it.

---

## 0. RCA — verified 2026-09-05

| Symptom | Cause | Where (current line numbers) |
|---|---|---|
| A create + track-add succeeds against Spotify, but the stored revision for the playlist stays `null`, so every later ADD posts `base_revision = null`. | The create's `/changes` 200 is folded by `OpRebaseStrategy.CaptureChangesResponse`; when the body is empty, or carries no storable 24-byte resulting revision, nothing is stored **and nothing is logged**. | `Backend/Mutation.cs:252` (`if (bytes.Length == 0) return;` — silent) and `:268-270` (`if (!storable && rev is not null) …RootlistBadRevision` — the `rev is null` case never warns). The null case then falls through to `:300` `else if (storable)` and does nothing. |
| With a null base, Spotify answers `changes_require_resync` (field 20) and we never advance. | Correct per invariant I4: `:261-266` marks the resync queue and returns. The revision stays null, so the NEXT edit has a null base too — a stable loop. | `Backend/Mutation.cs:144-159` (`Replay`: `baseRev = storedRev ?? op.BaseRev`), `:261-266`. |
| The queued revalidation never visibly ran (no `playlist.snapshot` line for the uri). | `LibrarySync.DrainWritesAsync()` does `TakeAll()` → `MarkDirty` → `PlaylistRevalidateAsync`; a throw is caught and logged **at Info, on the sync logger only**, the uri has already left the queue, and nothing retries. `MarkDirty` only helps on the next `OpenPlaylist` — which never comes while the page is already open. There is no log when a uri is taken, none when it converges, none when it falls back. | `Backend/Sync/LibrarySync.cs:960-966`, `:923-935`. |
| The user sees nothing wrong: the page is editable and looks fine. | The only pending affordance is `PlaylistPendingChip` (outbox count), which is 0 the moment the op is acked — even though the client cannot prove its copy matches the server's. | `Features/Detail/PlaylistInlineEdit.cs:160-188`. |

### RCA corrections (the earlier investigation is STALE on these)

1. **`LiveSessionHost.cs:723` does NOT bypass the single-writer loop.** The RCA read
   `playlistMutations.ScheduleDrain = ct => sync.DrainWritesAsync(ct)` as a direct drain. It is not:
   `LibrarySync.DrainWritesAsync(CancellationToken)` (`LibrarySync.cs:263-269`) writes a `SyncCommand(SyncKind.DrainWrites,
   Done: tcs)` onto the same channel the sibling seam at `:712` uses via `sync.Enqueue(...)`, and awaits the `Done`
   barrier. The only difference is awaitable vs fire-and-forget — which is deliberate (`PlaylistMutationSource.DrainAsync`
   at `PlaylistMutationSource.cs:508-533` needs the barrier to report Pending/terminal outcomes). **No wiring change
   there.** The plan's "serialize drains" item becomes: close the gaps that exist (§2.3).
2. **`Actions/Menus.cs:478-502`**: the add-tracks call is at `Menus.cs:427` and `:489` (`lib.AddTracksAsync(uri, tracks)`);
   `:489` is inside the cited range, `:427` is a second caller.
3. **Gate numbering in `PlaylistPushAsync`** is current: gate 1 echo (`:595`), gate 2 tombstone (`:600`), gate 3 local
   intent pending → `MarkDirty` (`:605-611`), gate 4 new head → revalidate if open else dirty (`:618-623`), gate 5
   resident + parent-rev match → apply in place (`:628-661`), gate 6 open → revalidate, else dirty (`:664-665`).
   Because the stored revision is null, gate 5 can never parent-match for the affected playlist — every foreign push
   for it goes to gate 6, and a cold playlist just goes dirty. That is why foreign edits only ever landed incidentally.
4. The resync queue is one shared instance: `App/Services.cs:661` creates it, `:666-667` hand it to `OpRebaseStrategy`
   and `CreatePlaylistStrategy`, `:798` publishes it as `svc.RealResyncQueue`, `SpotifyLive/LiveSessionHost.cs:655`
   passes it to `LibrarySync`. No identity mismatch (the queue's own logic is sound — `PlaylistResyncQueue.cs`, 36 lines).

### Why the revalidation "never ran" — ranked hypotheses the new logs will settle

The static evidence supports all three; only live logs (§2.4) can pick one:

| # | Hypothesis | How the new logs decide it |
|---|---|---|
| A | The create reply had no storable revision (empty body or no `resulting_revisions`/`revision`) → stored rev stays null → every ADD gets field 20 → `Mark` → `TakeAll` → `FetchPlaylistDiffAsync` with `rev == null` → **full GET threw** (e.g. a 404 while the create propagates) → caught at `LibrarySync.cs:965` as `sync: post-drain resync of '…' failed` (Info, sync logger) → never retried. | `changes.revision.missing` Warn on the create, then `resync.taken` → `resync.failed` with the exception message. |
| B | Same first half, but the GET succeeded — the `playlist.snapshot` line was missed (it is Info-level on the `playlist` category and gated by `IsEnabled(Info)` at `PlaylistFetcher.cs:284`). | `resync.converged` Info with `outcome=FellBackToFull` right after `resync.taken`. |
| C | The op that got the 200 was drained INLINE (`PlaylistMutationSource.PumpAsync` `:94` / `DrainAsync` `:513` `else` arms run `_mut.Drain` directly when `ScheduleDrain` is null), so `TakeAll()` — which lives only in `LibrarySync.DrainWritesAsync()` — did not run until the next loop drain. | `resync.taken` appears much later than `changes.syncresult.torn`, or only after a later user action. |

---

## 1. The convergence path, as built

```
  UI (create / add)                     MutationEngine.Drain                    LibrarySync loop (single writer)
  ───────────────────                   ────────────────────                    ────────────────────────────────
  PlaylistMutationSource                OpRebaseStrategy.Replay
    .CreatePlaylist  ─┐                   POST /playlist/v2/{id}/changes
    .AddTracksAsync  ─┤ enqueue op         base_revision = storedRev ?? op.BaseRev   ← null forever once lost
                      │                   200 → CaptureChangesResponse
                      ▼                        ├ body empty ──────────────► return          (SILENT)   §2.1
   ScheduleDrain(ct) ──► sync.DrainWritesAsync(ct)  (awaitable enqueue, :263)
                            │                  ├ multiple_heads / field 20 ► resync.Mark(uri) + torn log
                            ▼                  ├ sync_result ops ─────────► apply, adopt rev
             DrainWritesAsync() (:952)         ├ contents ────────────────► replace, adopt rev
               _mutations.Drain(...)           └ rev-only: storable? ─────► adopt : (nothing, SILENT)   §2.1
               foreach uri in _resync.TakeAll()            ▲
                 MarkDirty(uri)                             │ rev is null → never warns (:270)
                 PlaylistRevalidateAsync(uri) ── throws ──► Info log, uri gone, no retry             §2.2
                   FetchPlaylistDiffAsync → rev null → FetchPlaylistAsync → AdoptSnapshot → playlist.snapshot
```

---

## 2. Changes

### 2.1 Make revision loss loud — `Backend/Mutation.cs` `CaptureChangesResponse` (`:249-302`)

Two new Warn events, both keyed by uri. The empty-body arm currently returns before anything can be said about the uri;
the no-revision arm warns only when a malformed revision is present. Replace `:249-252` and `:268-270`:

```csharp
internal static void CaptureChangesResponse(IStore store, PlaylistResyncQueue resync, string uri, byte[] body)
{
    var bytes = SpotifyZstd.MaybeDecompressZstd(body);
    if (bytes.Length == 0)
    {
        // A 2xx with no body is a reply we cannot fold: no revision to adopt, no ops, no contents. Not an error on
        // the wire — but it leaves the stored revision where it was (possibly null), and the NEXT edit will post
        // that base. Never silent: this is exactly how a playlist ends up with base_revision = null forever.
        PlaylistMutationDiagnostics.ChangesEmptyBody(uri, body.Length);
        return;
    }
    Pl.SelectedListContent slc;
    try { slc = Pl.SelectedListContent.Parser.ParseFrom(bytes); }
    catch
    {
        PlaylistMutationDiagnostics.DealerDrop("playlist/changes", "unparseable", bytes.Length);
        return;
    }

    if (slc.MultipleHeads || slc.ChangesRequireResync)
    {
        resync.Mark(uri);
        PlaylistMutationDiagnostics.SyncResultTorn(uri, slc.MultipleHeads ? "multiple-heads" : "requires-resync");
        return;
    }

    var rev = PlaylistWireMapper.LastResultingRevision(slc);
    bool storable = PlaylistRevisions.IsWellFormed(rev);
    if (!storable)
    {
        // Both halves are loud now. A malformed head keeps the old RootlistBadRevision line (length + source); a
        // MISSING head is the new one — the reply carried neither resulting_revisions nor revision, so whatever this
        // fold does below, the stored revision cannot advance.
        if (rev is not null) PlaylistMutationDiagnostics.RootlistBadRevision(rev.Length, "changes-response");
        else PlaylistMutationDiagnostics.ChangesRevisionMissing(uri, bytes.Length,
                 hasSyncResult: slc.SyncResult is not null, hasContents: slc.Contents is { Items.Count: > 0 });
    }
    // … the rest of the method is unchanged (:272-301) …
}
```

Also fold the fact into the torn arm's field set: `SyncResultTorn(uri, reason)` gains a `baseWasNull` field so the log
shows whether field 20 came back against a null base (hypothesis A's signature). Signature:
`SyncResultTorn(string playlistUri, string reason, bool baseWasNull)`; the caller passes
`store.PlaylistRevision(uri) is null`.

### 2.2 The post-drain resync: retry, resolve, and log every outcome

#### 2.2.1 `Backend/Playlists/PlaylistResyncQueue.cs` — a lifecycle, not a set

The queue currently forgets a uri the moment `TakeAll()` runs. It has to remember it until the revalidate actually
converges, count failures, and tell the UI. Still engine-free (System only), still one shared instance.

```csharp
namespace Wavee.Backend.Playlists;

/// <summary>Invariant I4 — "never advance a revision past ops you did not apply", carried across the drain boundary,
/// and now the ONE record of whether that revalidation has happened yet.
/// <para>Lifecycle per uri: <see cref="Phase.Marked"/> (a /changes reply could not be folded) → <see cref="Phase.Revalidating"/>
/// (the loop took it) → gone (<see cref="Resolve"/> — ANY path that adopts a fresh snapshot/diff for the uri), or
/// <see cref="Phase.Failed"/> (the revalidate threw; attempts counted; the loop retries with backoff, and the page can
/// show it). <see cref="Changed"/> fires on the calling thread — the sync loop or a pool thread — so subscribers marshal.</para></summary>
public sealed class PlaylistResyncQueue
{
    public enum Phase : byte { None = 0, Marked = 1, Revalidating = 2, Failed = 3 }

    /// <summary>What the UI needs: the phase, when the uri was first marked (unix ms), how many revalidates failed.</summary>
    public readonly record struct Entry(Phase Phase, long SinceUtcMs, int Attempts)
    {
        public static readonly Entry None = default;
    }

    readonly object _gate = new();
    readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    readonly Func<long> _clock;

    /// <summary>Off-thread notification: the uri whose entry changed. One subscriber (LibraryBridge) — an Action, not an
    /// event, mirroring <c>PlaylistFetcher.onRevisionChanged</c>.</summary>
    public Action<string>? Changed;

    public PlaylistResyncQueue(Func<long>? clock = null)
        => _clock = clock ?? static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>A /changes reply for this uri could not be folded in place. Idempotent: a uri already tracked keeps its
    /// original stamp and attempts (so "how long has this been unresolved" is honest across repeated marks).</summary>
    public void Mark(string playlistUri)
    {
        if (string.IsNullOrEmpty(playlistUri)) return;
        lock (_gate)
        {
            if (_entries.TryGetValue(playlistUri, out var e))
            {
                if (e.Phase == Phase.Marked) return;                      // nothing changed
                _entries[playlistUri] = e with { Phase = Phase.Marked };  // Failed → Marked (a retry) / Revalidating → Marked
            }
            else _entries[playlistUri] = new Entry(Phase.Marked, _clock(), 0);
        }
        Changed?.Invoke(playlistUri);
    }

    /// <summary>Take every MARKED uri for revalidation. They stay tracked as <see cref="Phase.Revalidating"/> until
    /// <see cref="Resolve"/> or <see cref="Fail"/>. Empty is the common case (allocation-free early-out).</summary>
    public IReadOnlyList<string> TakeAll()
    {
        List<string>? taken = null;
        lock (_gate)
        {
            foreach (var (uri, e) in _entries)
                if (e.Phase == Phase.Marked) (taken ??= new List<string>()).Add(uri);
            if (taken is null) return Array.Empty<string>();
            for (int i = 0; i < taken.Count; i++) _entries[taken[i]] = _entries[taken[i]] with { Phase = Phase.Revalidating };
        }
        for (int i = 0; i < taken.Count; i++) Changed?.Invoke(taken[i]);
        return taken;
    }

    /// <summary>A retry is starting for a FAILED uri (the loop's scheduled retry or the page's Retry button).</summary>
    public bool TryBeginRetry(string playlistUri)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(playlistUri, out var e) || e.Phase != Phase.Failed) return false;
            _entries[playlistUri] = e with { Phase = Phase.Revalidating };
        }
        Changed?.Invoke(playlistUri);
        return true;
    }

    /// <summary>The uri converged (a snapshot or diff landed — whichever path did it). No-op when untracked.</summary>
    public void Resolve(string playlistUri)
    {
        bool removed;
        lock (_gate) removed = _entries.Remove(playlistUri);
        if (removed) Changed?.Invoke(playlistUri);
    }

    /// <summary>The revalidate threw. Returns the new attempt count (0 when the uri was not tracked).</summary>
    public int Fail(string playlistUri)
    {
        int attempts;
        lock (_gate)
        {
            if (!_entries.TryGetValue(playlistUri, out var e)) return 0;
            attempts = e.Attempts + 1;
            _entries[playlistUri] = e with { Phase = Phase.Failed, Attempts = attempts };
        }
        Changed?.Invoke(playlistUri);
        return attempts;
    }

    public Entry Get(string playlistUri)
    {
        lock (_gate) return _entries.TryGetValue(playlistUri, out var e) ? e : Entry.None;
    }
}
```

Existing tests keep passing: `Assert.Single(resync.TakeAll())` (`MutationOpRebaseTests.cs:159, 178`) still returns the
one uri; a second `TakeAll()` returns empty because the entry is now `Revalidating`, not `Marked`.

#### 2.2.2 `Backend/Sync/LibrarySync.cs` — one handler owns "revalidate a torn uri"

Replace the loop at `:960-966` and add a retry path. `PlaylistRevalidateAsync` resolves the entry on success so **every**
convergence path (open, push gate 4/6, the reconnect pass) clears it, not just the drain.

```csharp
// LibrarySync.cs — constants beside OpenRevalidateWindow (:60)
const int MaxResyncAttempts = 4;
static TimeSpan ResyncRetryDelay(int attempt) => TimeSpan.FromSeconds(attempt switch { 1 => 2, 2 => 10, _ => 30 });

// SyncKind gains one member; Dispatch (:317-333) gains one arm:
//   SyncKind.ResyncRevalidate => ResyncRevalidateAsync(cmd.Uri, cmd.Attempt),

async Task DrainWritesAsync()
{
    lock (_gate) _drainReenqueueScheduled = false;
    await _mutations.Drain(_mutationTransport, _ctx(), _ct).ConfigureAwait(false);

    // I4 — a /changes response that reported multiple_heads / changes_require_resync / a torn sync_result did NOT
    // advance the stored revision; it dropped the uri here instead. Converge it now, on the single writer.
    var torn = _resync.TakeAll();
    if (torn.Count > 0) PlaylistMutationDiagnostics.ResyncTaken(torn.Count, torn[0]);
    for (int i = 0; i < torn.Count; i++) await ResyncRevalidateAsync(torn[i], attempt: 0).ConfigureAwait(false);

    // … backoff block unchanged (:968-974) …
}

/// <summary>Revalidate ONE torn uri and record the outcome. Success is recorded inside PlaylistRevalidateAsync
/// (Resolve); a throw becomes Failed + a scheduled retry with backoff, up to MaxResyncAttempts, after which the entry
/// stays Failed for the page to show and the user to retry by hand.</summary>
async Task ResyncRevalidateAsync(string uri, int attempt)
{
    if (uri.Length == 0) return;
    if (attempt > 0 && !_resync.TryBeginRetry(uri)) return;   // resolved meanwhile (another path converged it) → nothing to do
    MarkDirty(uri);
    long started = Environment.TickCount64;
    try
    {
        var outcome = await RevalidateCoreAsync(uri).ConfigureAwait(false);
        PlaylistMutationDiagnostics.ResyncConverged(uri, outcome.ToString(), attempt, Environment.TickCount64 - started);
    }
    catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
    catch (Exception ex)
    {
        int n = _resync.Fail(uri);
        if (n < MaxResyncAttempts)
        {
            var delay = ResyncRetryDelay(n);
            PlaylistMutationDiagnostics.ResyncFailed(uri, n, ex.GetType().Name + ": " + ex.Message, (long)delay.TotalMilliseconds);
            ScheduleResyncRetry(uri, n, delay);
        }
        else PlaylistMutationDiagnostics.ResyncGaveUp(uri, n, ex.GetType().Name + ": " + ex.Message);
    }
}

void ScheduleResyncRetry(string uri, int attempt, TimeSpan delay)
{
    _ = Task.Run(async () =>
    {
        try { await Task.Delay(delay, _ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        Enqueue(new SyncCommand(SyncKind.ResyncRevalidate, uri, Attempt: attempt));
    });
}

// PlaylistRevalidateAsync (:923-935) splits: the public-facing shape stays for its existing callers, the core returns
// the outcome so the resync handler can log it, and Resolve runs on EVERY success.
async Task PlaylistRevalidateAsync(string uri) { if (uri.Length == 0) return; await RevalidateCoreAsync(uri).ConfigureAwait(false); }

async Task<DiffOutcome> RevalidateCoreAsync(string uri)
{
    var outcome = await _playlists.FetchPlaylistDiffAsync(uri, _ct).ConfigureAwait(false);
    switch (outcome)
    {
        case DiffOutcome.Applied: Interlocked.Increment(ref DiffApplied); break;
        case DiffOutcome.UpToDate: Interlocked.Increment(ref DiffUpToDate); break;
        default: Interlocked.Increment(ref DiffFellBack); break;
    }
    MarkRevalidated(uri); ClearDirty(uri);
    _resync.Resolve(uri);            // whichever path got here, the local copy now matches the server's head
    AfterNetworkSnapshot(uri);
    return outcome;
}
```

`WaitForIdleAsync` (`:286-291`) uses `PlaylistRevalidate` with an empty uri as the idle sentinel — the `uri.Length == 0`
guard stays at the top of `PlaylistRevalidateAsync`, so that contract is untouched.

The page's **Retry** (§2.5) enqueues `new SyncCommand(SyncKind.ResyncRevalidate, uri, Attempt: 1)` — `TryBeginRetry`
makes a retry on an already-resolved uri a no-op, so a stale button press costs nothing.

### 2.3 The inline-drain gap (hypothesis C) — `Backend/Playlists/PlaylistMutationSource.cs:89-98, 508-514`

When `ScheduleDrain` is null (pre-go-live, after `GoOffline`, tests), `PumpAsync`/`DrainAsync` call `_mut.Drain` inline and
nobody drains `_resync`. In production the transport is then the `StubTransport`, whose replies are never 200-with-a-body,
so no uri gets marked — the gap is theoretical today. Make it structurally impossible anyway: the `else` arms are the
only place a playlist edit can be drained off the loop, so log it.

```csharp
// PumpAsync (:93-94) and DrainAsync (:512-513) — the inline arm announces itself
if (ScheduleDrain is { } viaLoop) await viaLoop(ct).ConfigureAwait(false);
else
{
    PlaylistMutationDiagnostics.DrainInline(uri);     // a playlist write drained off the sync loop — resync convergence waits for the next loop drain
    await _mut.Drain(_transport, _ctx(), ct).ConfigureAwait(false);
}
```

(`DrainAsync` has no uri; pass the entity key through from `EnqueueEdit`'s callers — `DrainAsync(long edit, string uri, ct)` —
or log `"?"`. Prefer threading the uri; every caller already has it.)

### 2.4 Diagnostics — `Backend/Playlists/PlaylistMutationDiagnostics.cs`

New section after the P3 block (`:118-153`), same shape as the file (`WaveeLog.Instance.{Info|Warn}(Category, "id", "sentence", fields…)`):

```csharp
// ── I4 across the drain boundary: what the /changes reply carried, and whether the torn uri ever converged ──────────

/// <summary>A 2xx /changes reply with an EMPTY body. Nothing to fold, so the stored revision cannot advance — and if it
/// was null, every later edit posts base_revision = null and comes back changes_require_resync.</summary>
public static void ChangesEmptyBody(string playlistUri, int rawBytes) =>
    WaveeLog.Instance.Warn(Category, "changes.response.empty", "/changes 2xx carried no body — revision not advanced",
        WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("rawBytes", rawBytes));

/// <summary>A parseable /changes reply with NEITHER resulting_revisions NOR revision. Same consequence as above.</summary>
public static void ChangesRevisionMissing(string playlistUri, int bytes, bool hasSyncResult, bool hasContents) =>
    WaveeLog.Instance.Warn(Category, "changes.revision.missing", "/changes reply carried no resulting revision — revision not advanced",
        WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("bytes", bytes),
        WaveeLogField.Of("syncResult", hasSyncResult), WaveeLogField.Of("contents", hasContents));

public static void SyncResultTorn(string playlistUri, string reason, bool baseWasNull) =>
    WaveeLog.Instance.Info(Category, "changes.syncresult.torn", "could not fold the /changes response — revalidating",
        WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("reason", reason), WaveeLogField.Of("baseNull", baseWasNull));

public static void ResyncTaken(int count, string firstUri) =>
    WaveeLog.Instance.Info(Category, "resync.taken", "post-drain revalidation starting for torn playlists",
        WaveeLogField.Of("count", count), WaveeLogField.Of("uri", firstUri));

public static void ResyncConverged(string playlistUri, string outcome, int attempt, long ms) =>
    WaveeLog.Instance.Info(Category, "resync.converged", "torn playlist revalidated",
        WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("outcome", outcome),
        WaveeLogField.Of("attempt", attempt), WaveeLogField.Of("ms", ms));

/// <summary>The revalidate threw and a retry is scheduled. Warn, not Info: the page is showing a copy we cannot vouch for.</summary>
public static void ResyncFailed(string playlistUri, int attempt, string reason, long retryInMs) =>
    WaveeLog.Instance.Warn(Category, "resync.failed", "torn playlist revalidation failed — retrying",
        WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("attempt", attempt),
        WaveeLogField.Of("reason", reason), WaveeLogField.Of("retryInMs", retryInMs));

public static void ResyncGaveUp(string playlistUri, int attempts, string reason) =>
    WaveeLog.Instance.Warn(Category, "resync.gaveup", "torn playlist revalidation exhausted its retries — user retry only",
        WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("attempts", attempts), WaveeLogField.Of("reason", reason));

public static void DrainInline(string entityKey) =>
    WaveeLog.Instance.Info(Category, "mutation.drain.inline", "playlist write drained off the sync loop (ScheduleDrain unset)",
        WaveeLogField.Of("uri", entityKey));
```

Update the one existing `SyncResultTorn` call at `Mutation.cs:264` (and `:277`, `:287`) to the three-argument form.

### 2.5 User-facing "still syncing with Spotify" — the page says so when it cannot vouch for its copy

#### 2.5.1 Bridge — `App/LibraryBridge.cs`

The bridge already turns the outbox into per-uri UI signals (`PendingEdits(uri)`, `:111-135`). Add the same shape for
the resync entry. Wire it at the composition root beside `AttachMutations` (`Services.cs:793`):
`svc.LibraryBridge.AttachResync(resyncQueue);`.

```csharp
// LibraryBridge.cs — fields beside _pendingByUri (:36)
readonly Dictionary<string, Signal<PlaylistResyncQueue.Entry>> _resyncByUri = new(StringComparer.Ordinal);
PlaylistResyncQueue? _resync;

/// <summary>Attach the shared I4 resync queue. Called by the composition root beside <see cref="AttachMutations"/>.</summary>
public void AttachResync(PlaylistResyncQueue queue)
{
    _resync = queue;
    var post = _post;
    queue.Changed = uri => post(() => PublishResync(uri));   // the queue fires off-thread; signals are UI-thread only
}

/// <summary>The live I4 resync entry for ONE playlist — Marked/Revalidating while the server's head is not yet ours,
/// Failed when a revalidate threw, None once converged. Reading it subscribes the caller to that uri only.</summary>
public IReadSignal<PlaylistResyncQueue.Entry> ResyncState(string playlistUri)
{
    if (!_resyncByUri.TryGetValue(playlistUri, out var state))
    {
        state = new Signal<PlaylistResyncQueue.Entry>(_resync?.Get(playlistUri) ?? PlaylistResyncQueue.Entry.None);
        _resyncByUri.Add(playlistUri, state);
    }
    return state;
}

void PublishResync(string uri)
{
    if (_resync is null) return;
    if (_resyncByUri.TryGetValue(uri, out var state)) state.Value = _resync.Get(uri);
}
```

Note `Activate(post)` (`:61-68`) sets `_post` after construction; `AttachResync` is called from the Services ctor path,
so capture `_post` lazily inside the callback (`uri => _post(() => …)`) rather than at attach time.

#### 2.5.2 Pure rule — `Features/Detail/PlaylistSyncHealthRules.cs` (new, engine-free, source-included by `Wavee.Tests`)

```csharp
namespace Wavee;

/// <summary>What the playlist header's sync chip shows. <see cref="None"/> is the overwhelmingly normal state.</summary>
public enum PlaylistSyncHealth : byte { None = 0, Syncing = 1, Failed = 2 }

/// <summary>The PURE rule behind the header's "still syncing with Spotify" chip. Engine-free (System + the Backend
/// entry record) so PlaylistSyncHealthRulesTests pins it against production code.
/// <para>A torn /changes reply is normal and resolves in a few hundred milliseconds; showing a chip for that would be
/// noise. It becomes news when the uri has been unresolved longer than <see cref="SyncingAfterMs"/>, or when a
/// revalidate actually FAILED (that is lasting news at once — a retry is minutes away, or never).</para></summary>
static class PlaylistSyncHealthRules
{
    public const long SyncingAfterMs = 3_000;

    public static PlaylistSyncHealth Decide(in Wavee.Backend.Playlists.PlaylistResyncQueue.Entry entry, long nowUtcMs)
        => entry.Phase switch
        {
            Wavee.Backend.Playlists.PlaylistResyncQueue.Phase.None => PlaylistSyncHealth.None,
            Wavee.Backend.Playlists.PlaylistResyncQueue.Phase.Failed => PlaylistSyncHealth.Failed,
            _ => nowUtcMs - entry.SinceUtcMs >= SyncingAfterMs ? PlaylistSyncHealth.Syncing : PlaylistSyncHealth.None,
        };

    /// <summary>How long until an unresolved entry crosses the threshold (0 when it already has, or is not pending).
    /// The chip arms one UseTimeout for exactly this, so it appears the frame the threshold passes without polling.</summary>
    public static long MsUntilSyncing(in Wavee.Backend.Playlists.PlaylistResyncQueue.Entry entry, long nowUtcMs)
        => entry.Phase is Wavee.Backend.Playlists.PlaylistResyncQueue.Phase.Marked or Wavee.Backend.Playlists.PlaylistResyncQueue.Phase.Revalidating
            ? Math.Max(0, SyncingAfterMs - (nowUtcMs - entry.SinceUtcMs))
            : 0;
}
```

#### 2.5.3 Chip — `Features/Detail/PlaylistInlineEdit.cs`, beside `PendingChip` (`:160-188`)

Mounted in the same chips row at `:572`: `[StatusChip(status), PendingChip(uri), SyncChip(uri)]` /
`[PendingChip(uri), SyncChip(uri)]`. Zero-size when `None`, like `PendingChip`.

```
  ┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
  │  Road trip 2026                                          [ ⟳ 2 changes pending ] [ ⟳ Still syncing with Spotify… ] │  Syncing
  │  Christos · 14 songs · 52 min                                                                    │
  ├──────────────────────────────────────────────────────────────────────────────────────────────────┤
  │  Road trip 2026                        [ ⚠ Couldn't confirm this playlist with Spotify  Retry ] │  Failed
  └──────────────────────────────────────────────────────────────────────────────────────────────────┘
```

```csharp
internal static Element SyncChip(string uri)
    => Embed.Comp(() => new PlaylistSyncChip(uri)) with { Key = "pl-sync:" + uri };

/// <summary>The header's SYNC chip: the client cannot yet prove its copy of this playlist matches Spotify's head. A
/// sibling of the pending chip, deliberately separate: "pending" is about OUR edits not having landed; this is about
/// the SERVER's answer not having been folded — a torn /changes reply whose revalidation is taking too long or failed.</summary>
sealed class PlaylistSyncChip : Component
{
    readonly string _uri;
    public PlaylistSyncChip(string uri) => _uri = uri;

    public override Element Render()
    {
        var lib = UseContext(LibraryBridge.Slot);
        var svc = UseContext(Services.Slot);
        if (lib is null) return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        var entry = lib.ResyncState(_uri).Value;            // subscribe → this uri only
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var health = PlaylistSyncHealthRules.Decide(entry, now);
        // Re-render exactly when a still-pending entry crosses the threshold (no polling; the timer re-arms per entry).
        var crossed = UseState(0);
        long wait = PlaylistSyncHealthRules.MsUntilSyncing(entry, now);
        UseTimeout(() => crossed.Value++, wait > 0 ? wait : 0f, DepKey.From(HashCode.Combine(entry.Phase, entry.SinceUtcMs, entry.Attempts)));
        if (health == PlaylistSyncHealth.None) return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };

        bool failed = health == PlaylistSyncHealth.Failed;
        var children = new List<Element>(3)
        {
            Ui.Icon(failed ? Icons.Warning : Icons.Refresh, 12f, failed ? Tok.TextCaution : Tok.TextTertiary),
            new TextEl(Loc.Get(failed ? Strings.Detail.Edit.SyncFailed : Strings.Detail.Edit.SyncingWithSpotify))
                { Size = 11f, Weight = 600, Color = Tok.TextSecondary },
        };
        if (failed && svc?.RealSync is { } sync)
            children.Add(Button.Create(Loc.Get(Strings.Detail.Edit.RetrySync),
                () => sync.Enqueue(new Wavee.Backend.Sync.SyncCommand(Wavee.Backend.Sync.SyncKind.ResyncRevalidate, _uri, Attempt: 1)),
                ButtonAppearance.Subtle, ControlSize.Small));

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Shrink = 0f,
            Padding = new Edges4(8f, 3f, 10f, 3f), Corners = CornerRadius4.All(12f),
            Fill = Tok.FillSubtleSecondary,
            Enter = new EnterExit(Opacity: 0f, Active: true),
            Children = children,
        };
    }
}
```

`UseTimeout(Action, float ms, DepKey deps)` is `RenderContext.Timers.cs:295` in the engine; when `wait == 0` and the entry
is pending, the callback fires immediately and the extra render is a no-op (health already `Syncing`). Use the existing
icon/tone tokens if `Icons.Warning`/`Tok.TextCaution` are named differently in `WaveeTokens.cs` — check before use.

#### 2.5.4 Loc — `assets/loc/en-US.json`, inside `detail.edit` (after `"pendingSync"` at `:215`)

```json
"syncingWithSpotify": "Still syncing with Spotify…",
"syncFailed": "Couldn’t confirm this playlist with Spotify.",
"retrySync": "Retry",
```

Generated as `Strings.Detail.Edit.SyncingWithSpotify` / `.SyncFailed` / `.RetrySync` (the generator maps `detail.edit.pendingSync`
→ `Strings.Detail.Edit.PendingSync` today).

### 2.6 Tests (`Wavee.Tests`, no source-text tests)

| Test | Pins |
|---|---|
| `PlaylistResyncQueueTests` (new) | `Mark` twice keeps the original `SinceUtcMs`; `TakeAll` returns only Marked and moves them to Revalidating; a second `TakeAll` is empty; `Fail` increments attempts and phases Failed; `TryBeginRetry` only from Failed; `Resolve` removes and fires `Changed`; `Get` on an unknown uri is `Entry.None`. Injected clock. |
| `PlaylistSyncHealthRulesTests` (new) | None → None; Failed → Failed regardless of age; Marked at 2 999 ms → None, at 3 000 ms → Syncing; `MsUntilSyncing` arithmetic incl. the clamp at 0 and `0` for None/Failed. |
| `MutationOpRebaseTests` (extend `:163`) | `Changes200_ChangesRequireResync_MarksDirtyNotAdvance` additionally asserts `resync.Get(uri).Phase == Marked`. New: `Changes200_EmptyBody_DoesNotAdvance_AndDoesNotMark` (a `Resp(true, [], 200)` leaves the revision and the queue untouched — the WARN is the observable in live logs; the test pins the store/queue state). New: `Changes200_NoResultingRevision_DoesNotAdvance` (an `slc` with no `resulting_revisions` and no `revision`). |
| `LibrarySyncTests` (the `SyncHarness` at `LibrarySyncTests.cs:41-65` already shares `Resync` between the strategy and the loop) | `PostDrainResync_ThatThrows_IsRetried_ThenResolves`: script the playlist GET to fail once then succeed; after `DrainWritesAsync` + the retry delay (collapse `ResyncRetryDelay` via a settable field the way `ResyncWindow` is public at `:99`), `Resync.Get(uri).Phase == None` and `PlaylistGets == 2`. `PostDrainResync_ExhaustsRetries_StaysFailed`: always-failing GET → after `MaxResyncAttempts` the phase is Failed and no further GETs happen. `AnyConvergencePath_Resolves`: mark a uri, then push a gate-4 new head for the OPEN uri → `Resolve` ran (phase None) without a drain. |

### 2.7 Live verification — required before shipping, cannot be settled statically

**What field 20 (`changes_require_resync`) actually means.** `MutationOpRebaseTests.cs:163-179` codifies, from an
August 2026 capture, that field 20 means "the accepted delta cannot be expressed against your base — refetch". The proto
comment at `SpotifyLive/Protos/playlist4_external.proto:222-225` says the same and cites the 2026-08-15 capture. Two
readings are still open:

- (i) it is set only when the base is missing/stale (our null-base loop), and a healthy edit against the right base
  comes back rev-only or with `sync_result` — in which case §2.1-2.2 alone stop the loop after ONE full GET;
- (ii) it is set on every `/changes` reply — in which case every edit costs a full GET after this plan (functional, but
  a herd for a big playlist), and the right fix is to adopt `resulting_revisions[^1]` when field 20 is set AND the
  reply also carries a well-formed head AND our own ops are the only delta.

**How to confirm (one capture, no tooling prescribed):** on a build with §2.4 in place, run one add against a scratch
playlist you own, twice — first with a null stored revision (a fresh create), then after a full GET has stored the
24-byte head. Read the two `changes.syncresult.torn` / `changes.syncresult.applied` lines: `baseNull=true` followed by a
`torn requires-resync`, then `baseNull=false` followed by `applied`/rev-only, is reading (i); a second `torn` with
`baseNull=false` is reading (ii). To read the raw body, log `bytes.Length` and the parsed field set (already in the new
events) or, if the body itself is wanted, write it to the scratchpad from `CaptureChangesResponse` on a local build and
decode it with `Pl.SelectedListContent.Parser` — the golden fixture pattern at
`Wavee.Tests/Fixtures/playlist-wire/a178-create-response.bin` is where such a capture would live if it becomes a test.
**Do not change `MutationOpRebaseTests.cs:163`'s assertion until that capture exists**; if reading (ii) holds, add the
adopt-head arm described above and a second test for it.

---

## 3. Order of work

1. §2.1 + §2.4 (loud logs) — one file each, zero behaviour change, ship-safe alone.
2. §2.2 (queue lifecycle + retry + resolve) with §2.6's queue/rules/LibrarySync tests.
3. §2.3 (inline-drain log).
4. §2.5 (bridge + rule + chip + loc) with its tests.
5. §2.7 live capture; adjust `MutationOpRebaseTests.cs:163` only on evidence.

Gates before claiming done: `dotnet build Wavee.slnx` Debug **and** Release clean, `Wavee.Tests` green, and one live run
that shows `resync.taken` → `resync.converged` for a fresh create + add (or `resync.failed` → `resync.converged` on the
retry) and the `playlist.snapshot` line for that uri.

## 4. Files

| File | Change |
|---|---|
| `Backend/Mutation.cs` | §2.1 two Warn arms; `SyncResultTorn` 3-arg calls |
| `Backend/Playlists/PlaylistResyncQueue.cs` | §2.2.1 lifecycle (rewrite, ~90 lines) |
| `Backend/Playlists/PlaylistMutationDiagnostics.cs` | §2.4 nine events |
| `Backend/Sync/LibrarySync.cs` | §2.2.2 `ResyncRevalidateAsync`, `RevalidateCoreAsync`, `ScheduleResyncRetry`, `SyncKind.ResyncRevalidate`, `MaxResyncAttempts` |
| `Backend/Playlists/PlaylistMutationSource.cs` | §2.3 inline-drain log; thread the uri into `DrainAsync` |
| `App/Services.cs` | `svc.LibraryBridge.AttachResync(resyncQueue)` beside `:793` |
| `App/LibraryBridge.cs` | §2.5.1 `AttachResync`, `ResyncState`, `PublishResync` |
| `Features/Detail/PlaylistSyncHealthRules.cs` | new (§2.5.2); add to `Wavee.Tests.csproj` beside `PlaylistPageNoticeRules.cs` (`:139`) |
| `Features/Detail/PlaylistInlineEdit.cs` | §2.5.3 `SyncChip` + mount at `:572` |
| `assets/loc/en-US.json` | §2.5.4 three keys |
| `Wavee.Tests/{PlaylistResyncQueueTests, PlaylistSyncHealthRulesTests}.cs`, `MutationOpRebaseTests.cs`, `LibrarySyncTests.cs` | §2.6 |
