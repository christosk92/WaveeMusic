# Library Sync — Detailed Technical Implementation Plan

> Companion to `docs/library-sync-fix-proposal.md` (the diagnosis: root causes RC1–RC5 + wire-protocol appendix).
> This document is the **implementation-grade plan**: exact types, signatures, decision trees, freshness/retry/offline
> policies, anti-flicker mechanics, and a phased landing order with per-phase exit criteria.
>
> Grounding: every claim below was verified against the current code (file:line cites) plus three deep-dive dossiers:
> the playlist-open/render path, the transport/retry/offline surface, and the WaveeMusic reference's operational
> behaviors (reconnect, echo suppression, error handling). Where the reference has a known gap we deliberately do
> better — flagged `↑ better-than-reference`.

---

## 0. Design tenets

1. **One writer.** All library-state mutations from the network (rootlist, memberships, collection sets, revisions)
   flow through a single serialized loop (`LibrarySync`). Optimistic user writes stay inline (UI-frame latency), but
   their *replay/reconcile* runs on the same loop. No two code paths ever race a store write for the same entity.
2. **Offline-first is already correct — don't break it.** `CachedStore` bulk-loads SQLite at startup
   (`CachedStore.cs:38-41`); reads never block on network. Sync only *adds writers*; it never gates reads.
3. **Revision-gated work, anti-herd preserved.** A push for a cold playlist marks it dirty; nothing fetches until a
   user opens it (`DealerRouter.cs:62` today, kept). Resident + parent-rev match ⇒ zero-network in-place apply.
4. **The UI never flickers on a refresh.** Open-page freshness flows through in-place `Loadable.SetReady`
   (the `LibraryStore.Refresh` idiom, `LibraryStore.cs:80-88`) — **never** through re-keying a `UseAsyncResource`,
   whose seed-reset to `Empty` is precisely the skeleton flash (engine `RenderContext.cs:674`).
5. **Local intent wins until acknowledged.** A Pending outbox op shields its `(set, uri)` from inbound
   Confirmed writes; the server's echo/ack reconciles it, a terminal failure rolls it back.
6. **Every network interaction has a defined failure row** in the error taxonomy (§8). No new silent `catch { }`.

---

## 1. Architecture overview

```
                     UI thread                                 sync loop (1 bg task)                 network
 ┌──────────────────────────────────────────┐   Channel<SyncCommand>   ┌──────────────────────┐
 │ SaveButton / FollowButton / rows          │ ───────DrainWrites─────▶ │                      │──▶ POST /collection/v2/write
 │   └ LibraryBridge (optimistic Signal)     │                          │      LibrarySync     │──▶ POST /playlist/v2/…/rootlist/changes
 │ DetailPage / Sidebar / LibraryPage        │ ◀──post()──┐             │  (single consumer,   │──▶ GET  /playlist/v2/{id}/diff
 │   (in-place SetReady refresh)             │            │             │   owns all fetchers  │──▶ POST /collection/v2/delta|paging
 └──────────────────────────────────────────┘            │             │   + MutationEngine   │◀── LiveDealerTransport.Events("hm://")
                     ▲                                    │             │        .Drain)       │        (via DealerRouter → Enqueue)
                     │ StoreChange / CollectionsChanged   │             └──────────┬───────────┘
              ┌──────┴───────┐                            │                        │ store writes (lock-guarded)
              │  CachedStore │◀───────────────────────────┴────────────────────────┘
              │  (hot+SQLite)│
              └──────────────┘
```

- `LibrarySync` (new, `Wavee/Backend/Sync/LibrarySync.cs`) is the single runtime caller of
  `PlaylistFetcher.*`, `CollectionFetcher.FetchSetAsync`, `PlaylistDiffApplier.Apply` (network-sourced),
  and `MutationEngine.Drain`.
- `DealerRouter` keeps its parse/decode role but **stops writing the store**: it decodes topics + protos and
  enqueues typed commands. (Today it applies membership directly, `DealerRouter.cs:52-59` — that write moves
  into the loop so membership has exactly one network-side writer.)
- Optimistic applies (`MutationEngine.Save/Edit/Follow → ApplyOptimistic`) stay inline where the click happens —
  the store is lock-guarded (`InMemoryStore._gate`) so this is memory-safe; *logical* ordering vs inbound is
  handled by the pending-op shield (§7.2), not by forcing the optimistic write onto the loop.

---

## 2. New/changed components — full specs

### 2.1 `SwitchableTransport` (new — `Wavee/Backend/SwitchableTransport.cs`) — fixes RC2

Mirrors `SwitchableConnectivity` (`Connectivity.cs:34-59`):

```csharp
public sealed class SwitchableTransport : ITransport
{
    volatile ITransport _inner;
    public SwitchableTransport(ITransport initial) => _inner = initial;
    public ITransport Inner => _inner;
    public void SetInner(ITransport t) => _inner = t;
    // Request/Events/Requests/Reply/Publish → delegate to _inner.
    // Events/Requests: delegate per-subscription (subscribe the CURRENT inner). The mutation drain and
    // DealerRouter both live behind go-live, so no pre-swap subscription needs migration; document that
    // a SetInner does NOT re-home existing subscriptions (same contract as the other Switchables).
}
```

Wiring:
- `Services.CreateReal` (`Services.cs:159`): `var mutTransport = new SwitchableTransport(new StubTransport());`
  → pass to `EngineMutationSource` (`:162`). Expose `public SwitchableTransport? MutTransport { get; private set; }`
  alongside `RealStore` (`Services.cs:188-189`).
- `LiveSessionHost.StartAsync`, immediately before `svc.GoLive(...)` (`LiveSessionHost.cs:114`):
  `svc.MutTransport?.SetInner(transport);` — the same `LiveDealerTransport` built at `:79`.
- `Services.GoOffline` (`Services.cs:218`): `MutTransport?.SetInner(new StubTransport());` so logout returns
  writes to the inert stub (they queue in the durable outbox and replay on next login — §6.3).

### 2.2 `LibrarySync` (new — `Wavee/Backend/Sync/LibrarySync.cs`)

Placement in `Wavee/Backend/` keeps it unit-testable against `StubTransport.PushEvent` + crafted protos, like
`DealerRouter` and the fetchers.

```csharp
public enum SyncKind : byte { InitialHydrate, RootlistPush, PlaylistPush, CollectionPush, OpenPlaylist,
                              PlaylistRevalidate, DrainWrites, ReconnectResync }

public readonly record struct SyncCommand(
    SyncKind Kind,
    string Uri = "",                               // playlist uri / set id
    byte[]? ParentRev = null, byte[]? NewRev = null,
    IReadOnlyList<PlaylistOp>? Ops = null,
    TaskCompletionSource? Done = null);            // OpenPlaylist awaits completion (on-open path)

public sealed class LibrarySync : IAsyncDisposable
{
    public LibrarySync(IStore store, PlaylistFetcher playlists, CollectionFetcher collections,
                       MutationEngine mutations, ITransport transport, Func<SessionContext> ctx,
                       Func<string> username, Action<string> log, CancellationToken ct);

    public void Enqueue(in SyncCommand cmd);       // thread-safe; called by DealerRouter, Seam, EnsureFetched, boot
    public Task OpenPlaylistAsync(string uri, CancellationToken ct);  // enqueue + await Done (dedups in-flight per uri)
    public bool IsSetSyncing(string setId);        // optional UI hook (progress)
}
```

Internals (all decisions explicit):
- **Queue:** `Channel.CreateUnbounded<SyncCommand>(new UnboundedChannelOptions { SingleReader = true })`.
  One `Task.Run` consumer started in the ctor; `DisposeAsync` completes the writer and awaits the consumer.
- **Coalescing (inside the loop, before dispatch):** maintain `HashSet<string> _pendingSets` and
  `HashSet<string> _pendingPlaylists`. When a `CollectionPush("liked")` is already pending, a second enqueue is
  dropped at enqueue-time (interlocked set). Dealer bursts additionally get a **250 ms settle**: on the first
  push for a key, the loop schedules the fetch after `Task.Delay(250, ct)` and folds any pushes that arrive
  meanwhile (mirrors the reference's `DealerDebounce = 250ms`).
- **Per-command handlers** (each in try/catch with the §8 taxonomy; a handler failure never kills the loop):

  **`InitialHydrate`** (enqueued once on go-live):
  1. `Drain` the outbox **first** (local intent wins — see §6.3 for the ordering argument).
  2. Rootlist: revision-gated (§2.6). Under `store.BeginBulk()`.
  3. Each of the 5 logical sets (`liked, albums, artists, shows, episodes`, same list as
     `SpotifyLibrarySync.cs:19`): `collections.FetchSetAsync(set)` — token-gated delta, else full paging.
     Per-set failures isolated (log + record for retry, continue with the next set — the reference's
     per-collection catch pattern).
  4. Emit one `log` summary (counts per set, rootlist size).

  **`RootlistPush(parentRev, newRev, ops)`**: if stored rootlist revision == `parentRev` → apply ops to the
  rootlist entry list in memory (ADD/REM/MOV are positional over `RootlistEntry` rows) → `SetRootlist(next, newRev)`.
  Else → full `FetchRootlistAsync` (rootlists are small; a full GET is cheap and always converges).
  Either way, refresh the `"playlists"` saved-set fold (§2.8).

  **`PlaylistPush(uri, parentRev, newRev, ops)`** — the three-way gate (extends `DealerRouter.cs:52-62`):
  1. `stored == newRev` (byte-equal) → **echo of our own write** (we advanced the revision from the `/changes`
     response, §2.7) → no-op.
  2. Resident (`store.Membership(uri).Count > 0`) and `stored == parentRev` → apply ops in place
     (`PlaylistDiffApplier.Apply`), `SetMembership(uri, next, newRev)`, hydrate **only the added URIs**.
     Torn apply (`ArgumentOutOfRangeException`) → fall through to 3.
  3. Otherwise: if the playlist is **open** (see `_openUri` below) → enqueue `PlaylistRevalidate(uri)` (diff now);
     else mark stale only (`Resource.MarkStale` semantics — set a dirty bit in `_dirtyPlaylists`) — anti-herd.

  **`CollectionPush(set)`**: first try to parse the push payload as `PubSubUpdate` (§5 of the proposal appendix;
  proto already generated). If it parses **and** `client_update_id` matches one of our recent write ids (§7.1)
  → drop (echo). If it parses with items → apply the items directly through the pending-op shield (§7.2)
  — zero round-trip (`↑ better-than-reference`: they refetch for most sets). If unparseable/empty →
  `FetchSetAsync(set)` (token-gated delta — cheap). Note: a direct-applied push does **not** advance the sync
  token; the next delta re-delivers those items idempotently.

  **`OpenPlaylist(uri, done)`** (from `EnsureFetchedAsync`): dedup by uri (a second open while in-flight awaits
  the same task). If membership empty → full `FetchPlaylistAsync` (the page shows the existing skeleton path).
  Else if dirty-marked or `lastRevalidatedAt[uri]` older than **5 min** → run `PlaylistRevalidate` inline.
  Complete `done` when the store write lands.

  **`PlaylistRevalidate(uri)`**: revision-gated `/diff` (§2.6 decision tree). Updates `lastRevalidatedAt[uri]`,
  clears the dirty bit on success.

  **`DrainWrites`**: `await mutations.Drain(transport, ctx(), ct)` + schedule a follow-up `DrainWrites` after
  the backoff delay if `mutations.Pending > 0` (§8.3).

  **`ReconnectResync`** (§6.2): drain → rootlist revalidate → per-set delta → `PlaylistRevalidate` for
  (a) the open playlist and (b) dirty-marked resident playlists. Rate-limited: at most one per **30 s**
  (a flapping network must not storm).

- **Open-playlist awareness:** `public void SetOpenContext(string? uri)` — called by the DetailPage mount effect
  (§4.1) so pushes for the on-screen playlist revalidate eagerly instead of lazily.
- **No timers besides the two above** (250 ms settle, drain backoff). No polling loop.

### 2.3 `DealerRouter` changes (`Wavee/Backend/Realtime/DealerRouter.cs`)

Keep: single `Events("hm://")` subscription (`:29`), topic demux (`:34-35`), proto parse + parent-rev extraction.
Change:
- Constructor takes `LibrarySync sync` (or two delegates, keeping it seam-testable) instead of
  `markPlaylistStale`/`markCollectionStale` + `IStore`.
- `OnPlaylist`: add a **rootlist branch** — if the topic ends with `/rootlist`, parse
  `RootlistModificationInfo { new_revision(1), parent_revision(2), ops(3) }` (add the message to the playlist4
  proto if not generated yet) and enqueue `RootlistPush`. Otherwise parse `PlaylistModificationInfo` as today
  and enqueue `PlaylistPush(uri, parent, newRev, MapOps(info.Ops))`. **Remove the direct store write** — the
  in-place apply moves to the loop (single writer).
- `OnCollection`: pass the **raw payload** through: `sync.Enqueue(SyncCommand.CollectionPush(set, payload))`
  so the loop can attempt the `PubSubUpdate` parse (router stays parse-only for playlist4; collection payload
  interpretation lives with the collection logic).

### 2.4 Real collection write (rewrite `SetReplayStrategy.Replay`, `Mutation.cs:50-55`) — fixes RC4

New `Wavee/Backend/Collections/CollectionWriteMapper.cs`:

```csharp
public static class CollectionSets
{
    public static string WireSet(string setId) => …;   // MOVED from CollectionFetcher.cs:118-126 (single owner)
    public static string? UriPrefix(string setId) => …; // moved with it; CollectionFetcher now calls these
}

public static class CollectionWriteMapper
{
    /// body for POST /collection/v2/write
    public static byte[] BuildWrite(string username, string setId, string uri, bool saved,
                                    long nowUnixSeconds, string clientUpdateId)
        => new Col.WriteRequest {
               Username = username,
               Set = CollectionSets.WireSet(setId),               // "collection" | "artist" | "show" | "listenlater"
               Items = { new Col.CollectionItem { Uri = uri, AddedAt = (int)nowUnixSeconds, IsRemoved = !saved } },
               ClientUpdateId = clientUpdateId,
           }.ToByteArray();
}
```

`SetReplayStrategy.Replay` becomes:

```csharp
public async Task<bool> Replay(OutboxOp op, ITransport t, SessionContext ctx, CancellationToken ct)
{
    var cuid = Guid.NewGuid().ToString("N");
    var body = CollectionWriteMapper.BuildWrite(ctx.Account, op.SetId, op.EntityKey, op.TargetSaved,
                                                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), cuid);
    var headers = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
        { ["Content-Type"] = "application/vnd.collection-v2.spotify.proto",
          ["Accept"]       = "application/vnd.collection-v2.spotify.proto" };
    var r = await t.Request(Channel.Spclient, "/collection/v2/write", body, ct, method: "POST", headers: headers)
                   .ConfigureAwait(false);
    if (r.Ok) OnWriteAccepted?.Invoke(cuid);       // registers the echo id (§7.1)
    return r.Ok;
}
```

Notes pinned by the dossiers:
- `LiveDealerTransport.Request` stamps `application/protobuf` only when no Content-Type is supplied
  (`LiveDealerTransport.cs:66`) — passing the vendor type explicitly is mandatory (the gateway 400s on the
  generic type at the media-type layer, per the `CollectionFetcher.cs:18-20` comment).
- `method: "POST"` explicit — never rely on body-empty inference again (the RC4 bodyless-GET regression class).
- `ctx.Account` is the username: `SessionContext` is populated on go-live; `CollectionFetcher` uses the same
  `live.Username` — pass the same `Func<string> username` used at `SpotifyLibrarySync.cs:36` if `ctx.Account`
  isn't guaranteed non-empty (verify at wiring time; prefer one source).
- `added_at` is **int32 UNIX SECONDS** (collection), vs playlist timestamps in **int64 ms** — the classic trap.

### 2.5 Playlist follow = rootlist ADD/REM (new strategy) — fixes RC3

**Routing** (`Seam.cs:44-49`):

```csharp
public async Task SetSavedAsync(string uri, bool saved, CancellationToken ct = default)
{
    if (uri.StartsWith("spotify:playlist:", StringComparison.Ordinal)) _mut.Follow(uri, saved);
    else _mut.Save(SetForUri(uri), uri, saved);
    await _mut.Drain(_transport, _ctx(), ct).ConfigureAwait(false);
}
```

**`MutationEngine.Follow(string playlistUri, bool follow)`** — sibling of `Save` (`Mutation.cs:119-128`):
creates `OutboxOp(id, "rootlist", playlistUri, "playlists", follow, id, 0)`. Coalesces like "set"
(`KeyOf` = `rootlist|playlists|{uri}` — latest end-state wins; follow/unfollow toggles must not stack).

**`RootlistFollowStrategy : IMutationStrategy`** (`Type = "rootlist"`, `OfflineQueueable = true`):
- `ApplyOptimistic`: `store.SetSaved("playlists", uri, follow, SyncState.Pending)` (flips the pill this frame via
  the Saved fold, §2.8) **and** optimistically inserts/removes the rootlist entry (append at position 0 for a
  follow; remove the matching `Kind==0` row for an unfollow) so the sidebar reflects it immediately. The
  authoritative rootlist push/refetch reconciles ordering.
- `Replay`:
  1. Read the stored rootlist revision (§2.6). If none → `FetchRootlistAsync` first (one-time base).
  2. Build the op: follow → `Add { FromIndex = 0, Items = [Item { Uri, Attributes { Timestamp = nowMs, Public = true } }] }`;
     unfollow → `Rem { FromIndex = currentIndex(uri), Length = 1 }` (index from the *current* store rootlist).
  3. Body via an extended `PlaylistWireMapper.BuildRootlistChanges(baseRev, ops, username, nowMs)` — extends
     `BuildChanges` (`PlaylistWireMapper.cs:83-96`) with `Delta.Info { User, Timestamp }`,
     `WantResultingRevisions = true`, `WantSyncResult = true`, `Nonces = [random]`, and `ItemAttributes` on ADD
     items (today `BuildAdd` emits URI-only items, `:107-114`).
  4. `POST /playlist/v2/user/{username}/rootlist/changes` with the **first-party header set** (§2.7) and
     **`Content-Type: application/x-www-form-urlencoded`** (mandatory despite the binary protobuf body — without
     it the gateway 200-OKs a passive read handler and the write silently no-ops).
  5. On 200: parse the response `SelectedListContent` (zstd guard, §2.7), store the resulting rootlist +
     revision (the response IS the fresh rootlist). On **409**: refetch the rootlist (revision moved), return
     `false` → the outbox re-attempts against the fresh base (attempts-bounded). On other non-2xx: `false`.
- `Rollback`: `store.SetSaved("playlists", uri, !follow, SyncState.Confirmed)` + re-fetch rootlist (cheap,
  authoritative) to undo the optimistic entry edit.

Register in **both** wirings: `Services.cs:157-158` and `Scaffold.cs:25`.

**FollowButton/SaveButton need zero changes** — they call `LibraryBridge.ToggleSaved` (`SaveButton.cs:36,60`)
which lands in the new routing.

### 2.6 Revision-gated playlist `/diff` (new — `PlaylistFetcher.FetchPlaylistDiffAsync`) — fixes RC5

```csharp
public enum DiffOutcome { Applied, UpToDate, FellBackToFull }

public async Task<DiffOutcome> FetchPlaylistDiffAsync(string uri, CancellationToken ct = default)
```

- Wire: `GET /playlist/v2/{path}/diff?revision={enc}&handlesContent=&hint_revision={enc}` where
  `{enc} = Uri.EscapeDataString(FormatRevision(rev))` and
  `FormatRevision(byte[] rev)` = `"{BinaryPrimitives.ReadInt32BigEndian(rev[..4])},{Convert.ToHexStringLower(rev[4..])}"`
  (the comma percent-encodes to `%2C` — unencoded it triggers the gateway's nonstandard 509).
  `handlesContent=` must be present as an empty-valued param.
- Response tree:
  - **200 + `diff`** → `PlaylistWireMapper.MapOps(slc.Diff.Ops)` → apply onto `store.Membership(uri)` →
    `SetMembership(uri, next, slc.Diff.ToRevision)` → hydrate added URIs only → `Applied`.
  - **200 + `up_to_date`** or **304** → touch `lastRevalidatedAt` → `UpToDate`.
  - **509** (revision too stale — editorial mixes do this constantly) / torn apply / parse failure →
    `FetchPlaylistAsync(uri)` (full) → `FellBackToFull`.
  - No stored revision → straight to full fetch.
- **Rootlist revision storage** (prereq): the `rootlist` table has no revision column
  (`SqliteColdStore.cs:59`). Add to `IColdStore`: `byte[]? GetRootlistRevision()` /
  `void SetRootlistRevision(byte[]?)`, implemented over the existing `meta(key,value)` table
  (`SqliteColdStore.cs:49`) as hex text under key `rootlist_rev`. Extend
  `IStore.SetRootlist(IReadOnlyList<RootlistEntry> entries, byte[]? rev)` (overload; the old signature forwards
  null) + `byte[]? RootlistRevision()`; `InMemoryStore` holds a field, `CachedStore` dual-writes.
  `FetchRootlistAsync` (`PlaylistFetcher.cs:49-76`) stops discarding `slc.Revision` and passes it through.
- **On-open freshness** (`StoreLibrarySource.EnsureFetchedAsync`, `:106-107`): replace the empty-membership
  gate with the SWR split:

  ```csharp
  if (uri.StartsWith("spotify:playlist:", StringComparison.Ordinal))
  {
      if (_store.Membership(uri).Count == 0) { await sync.OpenPlaylistAsync(uri, ct); return; }  // blocking first fetch
      sync.Enqueue(SyncCommand.OpenPlaylist(uri));   // fire-and-forget revalidate (5-min window / dirty-gated)
      return;                                        // serve the cached membership NOW — no await, no flicker
  }
  ```

  The blocking branch preserves today's first-open skeleton; the revalidate branch is the SWR path — the page
  opens instantly from cache and the diff (usually a 304) lands via the in-place refresh (§4.1).
  `StoreLibrarySource` gains a `LibrarySync?` hook property (like `OnDemandFetch`), set in the go-live block;
  null offline/tests ⇒ pure store reads, unchanged.

### 2.7 Shared playlist-v2 mutation plumbing (new helpers)

- **`SpotifyHeaders.PlaylistV2Mutation(clientToken)`** (extend `Wavee/Backend/Spotify/SpotifyHeaders.cs`):
  `Content-Type: application/x-www-form-urlencoded`, `App-Platform`, `Spotify-App-Version` (reuse
  `SpotifyClientIdentity.AppVersionHeader`, `LiveSessionHost.cs:396`), `spotify-playlist-sync-reason: CAk=`,
  `Accept-Language: en`, `Cache-Control: no-store`, `spotify-accept-geoblock: dummy`,
  `spotify-dsa-mode-enabled: false`, `Origin`, `client-token`. Used by `RootlistFollowStrategy` **and**
  `OpRebaseStrategy.Replay` (`Mutation.cs:77-83` — today it posts bare, which the gateway silently no-ops;
  this is a latent RC-class bug in playlist *edits* that this plan fixes in passing).
- **Zstd guard**: `MaybeDecompressZstd(byte[] body, headers)` — check `Content-Encoding: zstd` *and* sniff the
  `28 B5 2F FD` magic. `/changes` and `/diff` responses can be zstd. (Reference note: .NET's automatic zstd
  HTTP decompression truncates multi-frame bodies — decode manually. Add `ZstdSharp` or a minimal decoder;
  if we'd rather avoid the dependency in Phase 4, send `Accept-Encoding: identity` on these two routes and
  revisit.)
- **`OpRebaseStrategy.Replay` upgrade**: parse the `/changes` 200 response as `SelectedListContent` and
  `SetMembership(uri, mapped, resultingRevision)` — today the response is discarded (`Mutation.cs:81-82`),
  which both loses the fresh revision (breaking echo suppression, §7.3) and leaves the local optimistic state
  unconfirmed. A 409 keeps returning `false` (outbox retries after a refetch, same as the follow strategy).

### 2.8 Fold followed playlists into `Saved` — fixes RC3 read side

- `LibrarySync`'s rootlist writes maintain the logical `"playlists"` saved-set: after any rootlist
  replace/patch, diff the entry set against `store.SavedUris("playlists")` — add missing as
  `SetSaved("playlists", uri, true, Confirmed)`, remove departed (skipping Pending-shielded ones, §7.2).
  Done inside the same `BeginBulk` as the rootlist write.
- `EngineMutationSource`:
  - `AllSets` (`Seam.cs:79`) gains `"playlists"` so `BuildUnion` includes it.
  - `OnStoreChange` (`Seam.cs:53-77`): the incremental branch calls `SetForUri(c.Uri)` which still lacks a
    playlist case — add `spotify:playlist:` → `"playlists"` to `SetForUri` (`Seam.cs:84-90`) so single-uri
    follow changes update `_saved` incrementally (the `"rootlist"` bulk bump already lands in the Bulk branch).
- `KindForSet` already maps `"playlists"` → `CollectionKind.Playlists` (`Store.cs:360`) — sidebar/lib pages
  get the right change kind for free.
- The pill then works with zero UI changes: `FollowButton` → `LibraryBridge.IsSaved` → `Saved` signal —
  in-place signal swap, no remount (dossier risk R8: verified safe).

---

## 3. Composition (go-live block, `LiveSessionHost.cs:118-163`) — fixes RC1

Inside the existing `if (svc.RealStore is { } store && metadata is { } md && …)` block, after the
`PlaylistFetcher` construction (`:124`):

```csharp
var collections = new CollectionFetcher(live.Pipeline, () => live.BaseUrl, () => live.Username, store,
    s => cold.GetCollectionRevision(s),
    (s, r) => cold.SetCollectionRevision(s, r, DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
    (uris, c) => md.SyncAllAsync(uris, c));
var sync = new LibrarySync(store, fetcher, collections, svc.RealMutations!, svc.MutTransport!,
                           () => sessionCtx, () => live.Username, log, cts.Token);
var router = new DealerRouter(transport, sync);                    // decode → enqueue
svc.RealLibrarySource!.Sync = sync;                                // on-open SWR hook (§2.6)
libSrc.OnDemandFetch = …existing…;                                 // album/artist branches unchanged
sync.Enqueue(new SyncCommand(SyncKind.DrainWrites));               // replay writes queued while logged out
sync.Enqueue(new SyncCommand(SyncKind.InitialHydrate));
connectivity.StatusChanged.Subscribe(…);                           // Reconnecting→Online ⇒ ReconnectResync (§6.2)
```

Plumbing prerequisites:
- `Services.CreateReal` exposes `RealCold` (the `SqliteColdStore`), `RealMutations` (the `MutationEngine`),
  `RealSessionHost`, and `MutTransport` next to `RealStore`/`RealLibrarySource` (`Services.cs:188-189`).
- `SessionContextHost.Current` must carry the real username post-login (it is constructed with `Account: ""`,
  `Services.cs:160-161`) — set it during go-live so `ctx.Account` is valid for write bodies.
- **Retire** the tracklist half of `HydratePlaylistHeadersAsync` (`LiveSessionHost.cs:241-253`) once
  `InitialHydrate` lands — header hydration (names/covers, `:227-239`) stays (it's presentation warm-up, and
  it iterates the now-authoritative rootlist), but it must not remain the only rootlist consumer.
- Dispose: `LibrarySync` and `DealerRouter` register on the host's owned `cts`/`DisposeAsync`
  (`LiveSessionHost.cs:168-175`), so logout tears the loop down before the transport.

---

## 4. On-playlist-open behavior & anti-flicker (the R1–R8 register, resolved)

The dossier's central finding: **the open DetailPage has no store subscription at all today** — its
`UseAsyncResource` is keyed on `route.Name` only (`DetailPage.cs:54`) and its nav preview is one-shot-consumed
(`NavPreview.cs:19`), so *any* re-key resets the seed to `DetailModel.Empty` → full-page skeleton flash.
Everything below exists to add live updates **without ever re-keying**.

### 4.1 DetailPage: in-place refresh (kills R1)

Add a mount effect to `DetailPage` (or `DetailShell`) when `kind == Playlist`:

```csharp
UseEffect(() => {
    svcSync?.SetOpenContext(uri);                     // eager pushes for the on-screen playlist (§2.2)
    var sub = store.Changes.Subscribe(c => {
        if (!c.IsBulk && c.Uri != uri) return;        // only this playlist (Bulk = re-read too; it's cheap)
        Debounce(150, async () => {                   // coalesce push bursts (reference ChangeBus window)
            var fresh = await LoadAsync(svc, kind, id, ct);
            post(() => model.SetReady(fresh));        // Ready→Ready in place: NO Pending, NO shimmer
        });
    });
    return () => { svcSync?.SetOpenContext(null); sub.Dispose(); };
}, route.Name);
```

- `Loadable.SetReady` is the flicker-free lever (`Loadable.cs:45`; the `LibraryStore.Refresh` idiom,
  `LibraryStore.cs:80-88`). The `UseAsyncResource` dep stays `route.Name` — untouched.
- `post` marshals to the UI thread (the `LibraryBridge.Activate` pattern, `LibraryBridge.cs:39-45`);
  the store change fires on the sync-loop thread.
- The 150 ms debounce absorbs a diff-apply + hydration burst into one re-map. `LoadAsync` →
  `JoinMembership` is O(n) dictionary lookups — fine at 10k rows off-thread.

### 4.2 TrackList: view-cache invalidation (kills R2 — a real latent bug)

`TrackList.View()` caches its filtered/sorted `int[]` keyed **only** on `(sort, query, flags)`
(`DetailTracks.cs:57,75-110`); `_tracks` is reassigned each render (`:180`). Today the tracklist never changes
post-open so this is masked; the moment §4.1 lands, a shrink ⇒ **`IndexOutOfRange`** via
`BoundTrackAt` (`:485-490`), a same-size change ⇒ wrong rows. Fix (mirror the `_lastCols` guard, `:200-201`):

```csharp
// in Render, after _tracks assignment (:180):
if (!ReferenceEquals(_lastTrackSet, model.Tracks)) { _lastTrackSet = model.Tracks; _viewKey = null; _tracksByTier.Clear(); }
```

Reference identity is the correct key: `JoinMembership` builds a fresh list per load, and an unchanged model
keeps the same list instance. Keep the `ContextUri` guard (`:189-195`) exactly as is — same-playlist refreshes
must NOT clear selection/scroll.

Also (R7): the `ItemsView` wrapper key stays `"list:t{tier}:d{density}:q{query}:f{flags}"` (`:267`) —
**never** add count/version/context terms to it. Count changes flow through the `ItemCount` prop and recycle
in place; `scrollKey: _route.Value.Name` (`:251`) preserves the scroll offset across the swap.

### 4.3 Sidebar: LibraryStore-backed playlists (kills R3 + R4 together)

Today the sidebar's playlist list is `UseAsyncResource(..., seed: Array.Empty, dep: PlaylistsVersion)`
(`WaveeSidebar.cs:87`) — dep never bumps on a rootlist sync (R4: no refresh), and if we bumped it, the
empty-seed reset shows 5 shimmer rows (R3: flash). Fix: read `LibraryStore.Playlists` (which already
subscribes `CollectionsChanged` and `Refresh`es in place, `LibraryStore.cs:97-126`) via the same
`Warm(store.EnsurePlaylists, store.Playlists)` idiom `LibraryPage` uses (`LibraryPage.cs:110-115`). Rows keep
`Flow.For(keyOf: uri)` + `PlaylistRow Key = "pl:"+uri` (`WaveeSidebar.cs:180-183,290`), so an inbound
follow/unfollow **animates the one row** (enter-fade / reflow-slide) instead of flashing the list.
`PlaylistsVersion` stays for user-created `wavee:playlist:*` (a different source).

### 4.4 Wholesale replace vs incremental patch — why we still prefer diffs

The store cannot distinguish them (both are one `SetMembership` + one `StoreChange`, `Store.cs:311-315`), and
after §4.1–4.3 neither flickers. Diffs still win on: (a) fetch cost (a 304 vs a 10k-row body), (b) hydration
cost (only added URIs hit `SyncAllAsync`), (c) cover-cache stability under memory pressure (R5: unchanged
`Track.Image.Url`s stay warm in the image cache; `MemoryGovernor` sheds prefetch-art first), (d) mosaic
stability for cover-less playlists (`MosaicTilesOf` recomputes per read, `StoreLibrarySource.cs:287-301`).

### 4.5 LibraryPage right pane

Selection-keyed `UseAsyncResource` (`LibraryPage.cs:79-81`) keeps its skeleton-on-selection-change (expected
UX). Do **not** wire store versions into its key. If live right-pane freshness is wanted later, apply the §4.1
pattern; out of scope for this plan (the master lists already refresh in place).

### 4.6 Realtime membership choreography (add / remove / move / curated reset)

Requirement: when a playlist changes live under the user, the tracklist should *narrate* the change — a moved
track slides to its new slot, an added track eases in, a removed track fades out and the list closes the gap,
and a curated playlist that is wholesale re-cut (editorial mixes replacing most of their content) gets a
deliberate "reset" treatment instead of a storm of row animations.

**Identity is already in place.** `PlaylistMember.ItemId` is the stable per-row key that survives reorders
(`PlaylistDiffApplier.cs:8-13`), and `JoinMembership` already stamps it onto every read-model row as
`Track.ContextUid` (`StoreLibrarySource.cs:269`). So the UI can compute a precise row-level diff **without any
new plumbing from the sync loop** — old list vs new list, keyed by `ContextUid` (fallback key for rows without
an ItemId: `ItemUri + occurrence index`). This also means a `/diff` apply, an in-place push apply, *and* a
full-refetch fallback all animate identically — the choreography never depends on which network path produced
the new list.

**Diff + classification** (new helper, `Wavee/Components/MembershipDiff.cs`, pure + unit-testable):

```csharp
public readonly record struct RowChange(string Key, int? OldIndex, int? NewIndex); // add: Old=null, remove: New=null
public sealed record MembershipDelta(IReadOnlyList<RowChange> Changes, double RetainedFraction, bool IsReset);
public static MembershipDelta Diff(IReadOnlyList<Track> old, IReadOnlyList<Track> next);
```

O(n) with two hash maps. Classification: `IsReset = RetainedFraction < 0.5 || changedRows > 40` — a curated
re-cut (REM-all + ADD-all, or a 509→full refetch that replaced most content) triggers the reset treatment;
everything under the threshold gets per-row choreography. Thresholds are constants next to the motion tokens,
tuned on-device.

**Choreography in the virtualized `TrackList`.** Rows are recycled bound slots in a fixed-height
`RepeatLayout.Stack(rowH)` (`DetailTracks.cs:231-251`) — fixed row height makes this a pure-transform FLIP
problem (`y = index · rowH`), and the engine's landed animation slab (`AnimValue` channels, `Element.Enter/
Exit/Layout` FLIP, `MotionTok`, reduced-motion-as-a-value) provides the primitives. On the in-place `SetReady`
(§4.1), before the swap renders:

1. **Anchor the viewport.** Find the first fully-visible row's key; after the swap, adjust the scroll offset by
   `(newIndex − oldIndex) · rowH` so the anchor row stays at the same screen Y (if the anchor was removed,
   anchor to its nearest surviving neighbor). Without this, an add/remove *above* the viewport visibly yanks
   the content — the single most jarring failure mode of live lists.
2. **Moves (surviving visible rows whose index changed):** FLIP — after the anchored swap, set the row's
   translateY channel to `(oldY − newY)` and spring it to 0 (`MotionTok` standard move, ~180 ms). Only rows
   intersecting the viewport (± one row) animate; off-screen rows just teleport — bounded work regardless of
   playlist size.
3. **Adds (new keys entering the viewport):** start at opacity 0 with a small translateY (−6 px), ease in
   ~160 ms, staggered ~20 ms per row, stagger capped at 8 rows (an over-cap batch lands together — no
   minute-long cascades).
4. **Removes (keys leaving):** the data row is gone after the swap, so exits render as **ghost overlays** — a
   snapshot row (title/artist text + cached cover, all already resident) drawn at the old Y in an overlay
   layer, fading out ~140 ms while the survivors' FLIP closes the gap. Cap: ≤ 12 ghosts; beyond that, skip
   ghosts and let the FLIP alone tell the story.
5. **Reset (`IsReset`):** no per-row animation — a single ~220 ms content crossfade of the list region (old
   frame fades out, new list fades in with a ~12 ms stagger on the first ~10 visible rows), plus the §4.1
   header refresh (curated playlists usually change cover/description in the same push). This reads as "the
   playlist was refreshed", which is the truthful narration for an editorial re-cut.
6. **Now-playing row:** the marquee row (`DetailTracks.cs:495-518`) follows its key through moves — the
   playing-track highlight must never detach from the track during a FLIP.

**Reduced motion** costs nothing: the engine's reduced-motion-as-a-value contract means the motion tokens
resolve to instant/fade-only — no branches in the choreography code (per the `one-experience-no-flags` rule,
this ships as ONE tuned look, no quality knobs).

**Engine dependency check (resolve before Phase 7 starts):** the plan needs per-row `translateY`/`opacity`
channels on recycled `ItemsView` slots + one overlay layer for ghosts. If `ItemsView` bound slots can't carry
per-slot transform channels today, the fallback is app-side: a transform prop threaded through
`BoundRowContent` (`DetailTracks.cs:523-552`) driven by a small `TrackListChoreographer` that owns the
`AnimValue` handles — no engine changes required, at the cost of ~1 prop per visible row. Spike this first;
whichever lands, the choreography module's API is the same.

**Sidebar is already covered:** rootlist rows are keyed (`Flow.For(keyOf: uri)` + `PlaylistRow Key`,
`WaveeSidebar.cs:180-183,290`) with `ItemReflowTransition` — an inbound follow animates in, an unfollow fades
out, a reorder slides. §4.3's switch to `LibraryStore.Playlists` is what activates these for *inbound* changes;
no extra work.

---

## 5. Caching & freshness model (single table)

| Data | Store | Durable key | Fresh when | Revalidate trigger | Mechanism |
|---|---|---|---|---|---|
| Collection sets (5) | `collection_items` + hot `_savedBySet` | `collection_rev(set_id)` (`SqliteColdStore.cs:53`) | always served; token-gated | push (`hm://collection`), reconnect, go-live | `/delta`, fallback `/paging` |
| Rootlist | `rootlist` table + hot | `meta["rootlist_rev"]` (new) | always served; revision-gated | push (`…/rootlist`), reconnect, go-live | in-place ops / full GET |
| Playlist membership | `playlist_items` + WARM LRU (`CachedStore.cs:24-65`) | `playlists.base_rev` | revision-gated + 5-min on-open window | push (parent-rev gate), open (SWR), reconnect (dirty+open only) | in-place ops / `/diff` / full |
| Playlist headers | `entities` | — | populated by rootlist hydration + fetches | rootlist change | header fetch |
| Track/album/artist entities | `entities` | etag inside `MetadataService` | 1 h etag (existing) | hydration calls | extended-metadata batch |
| Artist overview | `entities` | `FetchedAt` | 12 h TTL (existing, `StoreLibrarySource.cs:97`) | on open | Pathfinder |

Deliberate choices:
- **No blanket playlist TTL.** Playlists revalidate on push (event-driven), on open (5-min window, usually a
  304), and on reconnect if dirty/open. A background TTL sweep over N playlists is the herd the design
  explicitly avoids (`DealerRouter.cs:13-15` comment; `FreshnessPolicy.SnapshotRevision`, `Resource.cs:29,166`).
- **Full-paging reconcile is mark-and-sweep.** `CollectionDeltaApplier` only folds listed items
  (`CollectionDeltaApplier.cs:21-30`) — a full snapshot after a dead token would never *remove* items deleted
  server-side. In `CollectionFetcher.FetchSetAsync`'s paging branch (`CollectionFetcher.cs:63-77`), accumulate
  the snapshot's URI set and, after the last page (inside the same `BeginBulk`), remove
  `store.SavedUris(setId) − snapshot − pendingShielded` (§7.2). Delta branch unchanged (deltas carry removals).
- **Failed paging mid-loop** keeps today's semantics (partial items applied, token NOT advanced,
  `CollectionFetcher.cs:77` unreached ⇒ next attempt re-pages fully): safe/idempotent. The sweep only runs on
  a *completed* paging loop — never sweep on a partial snapshot (that would mass-delete).

---

## 6. Offline & network switches

### 6.1 Signals available (and their limits)

The only connectivity signal is the dealer socket's `Connectivity` (`Offline/Connecting/Online/Reconnecting`,
driven by `LiveDealerTransport.RunAsync`, `LiveDealerTransport.cs:107-137`), surfaced as `svc.Connectivity`.
There is **no OS network-change listener** in the codebase, and HTTP failures don't feed the signal. The dealer
socket has a 30 s ping / 70 s dead-man watchdog (`:38-39,180-204`), so a network switch is detected within
~70 s worst-case, typically at the next ping. This is sufficient — do **not** add an OS `NetworkChange`
listener in v1 (one more platform surface; the dealer watchdog already converges).

### 6.2 Reconnect resync (`↑ better-than-reference` — the reference silently loses pushes across a reconnect)

Subscribe `connectivity.StatusChanged` in the go-live block. On transition **to `Online` from
`Reconnecting`/`Offline`** (not the initial `Connecting→Online`), enqueue `ReconnectResync`:

1. `DrainWrites` — pending outbox first (local intent wins; replays are idempotent end-states).
2. Rootlist revalidate (revision-gated — a no-change reconnect costs one small GET; with `/diff` on the
   rootlist URI it's a 304).
3. Per-set `/delta` for all 5 sets (token-gated — near-free when nothing changed).
4. `PlaylistRevalidate` for the open playlist + dirty-marked **resident** playlists only. Cold playlists stay
   lazy (anti-herd) — they were dirty before, they stay dirty, they revalidate on open.

Rate limit: ≥30 s between resyncs; a flapping Wi-Fi→LTE→Wi-Fi switch coalesces to one pass.
**Answer to the design question "do `/diff` again on a network switch?": yes, but only revision/token-gated
and only for rootlist + collections + open/dirty-resident playlists — every step is a cheap no-op when nothing
changed, and the anti-herd contract survives.**

### 6.3 Offline writes & drain lifecycle

Today `Drain` fires **only** after each `Save` (`Seam.cs:48`); the durable outbox reloads on boot
(`Mutation.cs:111-113`) but nothing replays it until the next write. New drain triggers (all enqueue
`DrainWrites` — serialized on the loop):
1. **Post-write** (existing, kept — `SetSavedAsync` drains; it now goes through the loop-scheduled path in
   Phase 6, inline until then).
2. **Go-live** (§3) — replays writes made while logged out (they queued against the stub).
3. **Reconnect** (§6.2 step 1).
4. **Backoff timer** — while `Pending > 0` and the last drain had failures, schedule the next `DrainWrites`
   at `min(60 s, 1 s · 2^consecutiveFailures)` (§8.3). No periodic drain when the outbox is empty.

Ordering vs inbound: drain **before** delta on hydrate/reconnect. Rationale: `CollectionDeltaApplier` writes
`Confirmed` state; if a delta ran first it could visually revert a not-yet-sent like for the gap between delta
and drain. Draining first converges the server toward local intent, and the following delta then confirms it.
(The pending-op shield §7.2 covers the mid-flight window regardless.)

Offline UX: unchanged visually (hearts flip optimistically and persist — that part already works, RC2 note).
The `SyncState.Pending` rows in the store are the hook if a "pending sync" badge is ever wanted; not in scope.

---

## 7. Echo suppression & self-write reconcile

Three layers, cheapest first:

### 7.1 `client_update_id` matching (collections)

`LibrarySync` keeps a ring buffer (last 32) of `client_update_id`s from accepted collection writes (§2.4's
`OnWriteAccepted`). An inbound `PubSubUpdate` whose `ClientUpdateId` matches → dropped before any store work.
(`↑ better-than-reference`: the reference generates the id but never checks the echo — it relies on idempotency
alone.)

### 7.2 Pending-op shield (the general guard)

`MutationEngine` exposes:

```csharp
public bool HasPending(string setId, string entityKey)   // lock _gate; _outbox.ContainsKey($"set|{setId}|{entityKey}")
                                                          // + the "rootlist|playlists|{uri}" key
```

Every **inbound Confirmed** `SetSaved` (delta apply, direct push apply, rootlist fold, mark-and-sweep removal)
first checks `HasPending` — if a local intent is in flight for that `(set, uri)`, the inbound write is skipped.
The op's own drain reconciles it (`Confirmed` on ack, rollback on dead-letter). This is what makes
"like offline → reconnect → delta arrives before drain finishes" glitch-free.

### 7.3 Revision equality (playlists)

Because `OpRebaseStrategy`/`RootlistFollowStrategy` now store the **resulting revision** from their `/changes`
response (§2.7/§2.5), the dealer echo for our own edit arrives with `new_revision == stored` → the
`PlaylistPush` handler's first gate (§2.2) drops it. No settle-window heuristics needed (the reference's 5 s
`DealerSettleWindow` exists because it *didn't* always advance the revision synchronously; we do).

### 7.4 Store-level no-op elision (churn, not correctness)

`InMemoryStore.SetSaved` currently bumps + emits even when nothing changed (`Store.cs:282-299`). Add an
early-out: if `saved` and the `(set,uri)` already present with the same `SyncState` — or `!saved` and absent —
return without `Bump`. This turns every idempotent echo/delta-overlap into literal silence (no
`CollectionsChanged`, no `LibraryStore.Refresh`, no signal churn). Same guard in spirit as the
`EngineMutationSource.OnStoreChange` `now != was` check (`Seam.cs:67`).

---

## 8. Error handling & retry taxonomy

### 8.1 What the transport already gives us (don't duplicate)

- **401** on any spclient HTTP call: `AuthMiddleware` force-refreshes the token once and retries
  (`HttpAuth.cs:60-85`).
- **429**: `RateLimitMiddleware` honors `Retry-After` (≤30 s), up to 4 attempts (`HttpAuth.cs:88-112`).
- **Dealer socket**: reconnect with exp backoff 3→30 s, host rotation, fresh token per attempt, 70 s dead-man
  (`LiveDealerTransport.cs:107-204`).
- `ITransport.Request` returns `Resp.Ok=false` on non-2xx; **network exceptions propagate** — every loop
  handler wraps its awaits.

### 8.2 Per-operation policy

| Operation | Failure | Policy |
|---|---|---|
| `InitialHydrate` set fetch | non-200 / exception | isolate per set (others proceed); record the set; retry the failed sets once after 30 s; then leave to next trigger (push/reconnect/open). Log at warn. |
| `/collection/v2/delta` | `delta_update_possible == false` | fall through to full paging (existing, `CollectionFetcher.cs:52-61`). |
| Full paging mid-loop | exception | partial items committed, token NOT advanced (existing) → next attempt re-pages. **No sweep on partial** (§5). |
| `/diff` | 304 / `up_to_date` | fresh — touch timestamp. |
| `/diff` | 509 / torn apply / parse error | full `FetchPlaylistAsync` fallback. |
| `/diff`, full fetch | network exception | keep dirty bit; retry on next trigger (open/push/reconnect). On-open path: serve cache, swallow into log (never block the page). |
| `/collection/v2/write` | non-2xx | `Replay` returns false → outbox attempt++ (max 10) → dead-letter + rollback (existing `Mutation.cs:176-200`). |
| `/…/rootlist/changes`, `/…/changes` | **409** | refetch base (rootlist / playlist) so the stored revision advances, return false → next drain rebases against the fresh base. Attempts-bounded. |
| same | other non-2xx / exception | attempt++ → dead-letter path. |
| zstd decode failure | exception | propagate → caller's full-fetch fallback (diff) / attempt++ (writes). |
| Dealer push parse failure | — | ignore the frame (existing `DealerRouter.cs:42`); for collections, fall back to `FetchSetAsync` (§2.2). |

### 8.3 Drain backoff (fixes the burn-all-10-attempts-in-one-burst gap)

`MutationEngine.Drain` today retries with zero delay whenever called; a user rage-clicking hearts while
offline-ish could burn an op's 10 attempts in seconds. Add per-op scheduling **without touching the durable
schema**: an in-memory `Dictionary<long, DateTime> _nextAttemptAt`; `Drain` skips ops whose time hasn't come;
on failure set `nextAttemptAt = now + min(60 s, 1 s · 2^attempts)`. The loop's post-drain reschedule (§6.3.4)
guarantees a skipped op is re-visited. `MaxAttempts = 10` and dead-letter semantics unchanged. Rationale for
in-memory-only: after a restart, attempts reload from SQLite and the clock resets — acceptable (a restart is a
natural retry moment).

### 8.4 Dead-letter surfacing

Dead-lettered ops currently vanish into `dead_letter` + an in-memory list (`Mutation.cs:104,186`). Add one log
line at warn with `(type, entity, reason)` and — because the rollback flips the optimistic state back — the UI
self-corrects visibly. A toast is a possible follow-up; not in scope.

---

## 9. Performance engineering

- **Frame budget: zero impact by construction.** Everything network-side runs on the sync-loop task; the only
  UI-thread work is (a) the optimistic `Save/Follow` store write on click (one dictionary write + one signal),
  (b) `post`ed signal swaps, (c) re-running `LoadAsync`'s join off-thread then `SetReady` on-thread. The
  engine's zero-alloc phases 6–13 are untouched (this is all app-side; VerticalSlice gates unaffected).
- **Signal-storm control** (the layers, in order): store-level no-op elision (§7.4) → `BeginBulk` around
  hydrate/paging/rootlist+fold (one `StoreChange.Bulk` per burst, `Store.cs:371-382`) → 250 ms dealer settle in
  the loop (§2.2) → 150 ms UI debounce in the DetailPage effect (§4.1). Note `BeginBulk` suppression is
  store-wide (`Store.cs:369-371` comment): a user like during a bulk folds into the Bulk signal — correct,
  coarser; bulks are kept short (per-set, per-playlist — never one giant bulk around the whole hydrate).
- **Hydration cost**: diff/delta paths hydrate **only added URIs** (`PlaylistFetcher.HydrateAsync` filter
  pattern, `PlaylistFetcher.cs:87-96`); `MetadataService.SyncAllAsync` batches + etags. Initial full hydrate is
  the same work `--spotify-sync` does today, now amortized behind the offline-first render (UI is usable on
  cold cache from frame 1).
- **Allocation discipline**: `SyncCommand` is a readonly record struct through an unbounded channel (no
  boxing); the 32-entry echo ring is a fixed array; `PlaylistDiffApplier` mutates a single `List<>` copy of the
  resident membership (existing pattern, `DealerRouter.cs:54`); JoinMembership re-runs allocate O(n) lists per
  refresh — acceptable off-thread at ≤10k rows, and only for the open page, debounced.
- **Membership LRU interplay**: `CachedStore.TouchResident` caps resident baselines (128 / 24 MB,
  `CachedStore.cs:24-65`). The loop's in-place diff-apply reads `Membership(uri)` (promoting from cold if
  evicted — `CachedStore.cs:110-120`) before applying, so a push for an evicted-but-followed playlist works;
  if the promote misses cold too, gate 3 of `PlaylistPush` (mark dirty) catches it.
- **Cover/image stability**: unchanged URLs stay cache-hot (`Surfaces.Artwork` keyed URL+decodePx,
  `Surfaces.cs:60-83`) — the diff-first policy (§4.4) is what keeps a 10k-row playlist's scroll butter during a
  background refresh.

---

## 10. Concurrency & ordering (race matrix)

| Race | Resolution |
|---|---|
| Inbound diff vs inbound delta vs rootlist write | All on the single loop — serialized by construction. |
| Optimistic click-write vs loop store write, same uri | Store is lock-guarded (memory-safe). Logical order: inbound Confirmed writes pass the `HasPending` shield (§7.2); the drain (loop-side) reconciles. |
| Drain replay vs a newer coalesced Save | Existing identity-reconcile in `Drain` (`Mutation.cs:157-173`) — kept verbatim. |
| Two opens of the same playlist | `OpenPlaylistAsync` dedups per-uri on the loop (one fetch, both awaiters). |
| Open-fetch vs push-diff, same playlist | Both are loop commands → serialized; whichever lands second sees the other's revision and 304s/no-ops. |
| Reconnect resync vs in-flight InitialHydrate | Commands queue behind each other; the 30 s resync rate-limit plus token/revision gating make the second pass ~free. |
| Album/artist `OnDemandFetch` | Stays direct-await (different entity families; no membership/revision interplay). Playlist branch is the only one routed through the loop. |
| Logout mid-sync | Loop cancels via the host CTS (`LiveSessionHost.cs:170`); `GoOffline` swaps the mutation transport back to stub; queued commands drain to no-ops on the cancelled token. |

---

## 11. Telemetry & diagnostics

- One `log("sync", …)` line per: hydrate summary (per-set counts + duration), diff outcome per playlist
  (`Applied(nOps)/UpToDate/FellBackToFull`), reconnect resync trigger + duration, write replay failure
  (status + attempt), dead-letter, echo drops (counter, logged at debug).
- Counters on `LibrarySync` (test + probe visibility, mirroring `Resource.FetchCount`, `Resource.cs:43`):
  `DiffApplied/DiffUpToDate/DiffFellBack/DeltaRuns/PageRuns/EchoDropped/PushApplied/PushMarkedDirty`.
- Extend the existing `--spotify-sync` CLI to run **through `LibrarySync`** (same composition as the app,
  minus UI) so the one-shot becomes an integration probe of the real orchestrator instead of a divergent
  hand-wiring (`SpotifyLibrarySync.cs` shrinks to: connect → build → `InitialHydrate` → listen).

---

## 12. Phased landing plan

Every phase: builds clean, `Wavee.Tests` green, independently shippable. Live verification uses
`--real-backend` + login; cross-device checks use a phone/other client on the same account.

### Phase 0 — Groundwork (no behavior change; de-risks everything after)
- `TrackList` view-cache invalidation on track-set identity change (§4.2) — fixes the latent R2 bug.
- `InMemoryStore.SetSaved` no-op elision (§7.4).
- Sidebar playlists → `LibraryStore.Playlists` Warm/Refresh (§4.3).
- `MutationEngine.HasPending` (§7.2), unused yet.
- **Tests**: TrackList renders correctly after an in-place track-set swap (shrink/grow/reorder); SetSaved
  no-op emits nothing; sidebar renders identically from the store cell. **Exit**: app visually identical.

### Phase 1 — Inbound read sync composed (RC1) + open-page live refresh
- `LibrarySync` (commands: InitialHydrate, RootlistPush, PlaylistPush, CollectionPush, OpenPlaylist,
  PlaylistRevalidate — revalidate = full refetch for now; `/diff` comes in Phase 5).
- Rootlist revision storage (`meta["rootlist_rev"]`, `IStore.SetRootlist(entries, rev)`, §2.6 prereq).
- `DealerRouter` → decode-and-enqueue (+ rootlist topic branch); wire in the go-live block (§3).
- Collection full-paging mark-and-sweep (§5) with the pending shield.
- DetailPage in-place refresh effect + `SetOpenContext` (§4.1).
- **Tests**: loop drains InitialHydrate against `StubTransport` + crafted `SelectedListContent`/`PageResponse`
  fixtures (store contents + tokens asserted); `PushEvent` a playlist push → parent-rev in-place apply / dirty
  mark; rootlist push → fold updates `SavedUris("playlists")`; sweep removes server-deleted items but not
  Pending-shielded ones. **Live exit**: log in → sidebar populates without `--spotify-sync`; edit a playlist
  from the phone → open copy updates in place (no skeleton, scroll preserved); collections update on push.

### Phase 2 — Write transport promotion (RC2) + drain lifecycle
- `SwitchableTransport` + `Services.MutTransport` + go-live/`GoOffline` swaps (§2.1).
- Drain triggers: go-live, reconnect (basic subscription), backoff rescheduling (§6.3, §8.3).
- **Tests**: swap routes `Request` to the new inner; boot-with-pending-outbox drains on go-live; backoff skips
  not-due ops. **Live exit**: a like produces a real outbound HTTP call (transport log) — expected to *fail*
  against the wire until Phase 3; verify attempts/dead-letter behave (cap the live test with a stub).

### Phase 3 — Real collection writes (RC4) + echo suppression
- `CollectionSets` (WireSet moved), `CollectionWriteMapper`, `SetReplayStrategy.Replay` rewrite (§2.4).
- Echo ring (§7.1) + `PubSubUpdate` direct-apply in `CollectionPush` (§2.2).
- **Tests**: replay posts `/collection/v2/write`, vendor content-type, wire set names, `is_removed`, seconds
  timestamps (assert via `StubTransport.LastRequest*`, `Transport.cs:62-66`); echo id → push dropped; foreign
  push with items → applied through the shield. **Live exit**: like a song in Wavee → appears in Liked Songs
  on the phone within seconds; like on the phone → Wavee heart flips (via push) without a full resync; unlike
  round-trips; restart Wavee → state persists and matches the server.

### Phase 4 — Playlist follow via rootlist (RC3)
- `MutationEngine.Follow` + `RootlistFollowStrategy` + routing branch in `SetSavedAsync` (§2.5).
- `BuildRootlistChanges` (info/nonces/want-flags/attrs), `SpotifyHeaders.PlaylistV2Mutation`, zstd guard,
  `/changes` response revision capture for **both** rootlist and `OpRebaseStrategy` (§2.7).
- Saved fold: `AllSets` + `SetForUri` playlist cases (§2.8).
- **Tests**: follow routes to the rootlist strategy (never the "liked" set); wire body has base_revision +
  ADD-with-attributes / REM-at-index; 409 → refetch + retry; `BuildUnion` includes `"playlists"`;
  `IsSaved(spotify:playlist:…)` true after a rootlist fold; pill flips optimistically and rolls back on
  dead-letter. **Live exit**: follow a playlist → sidebar row animates in, pill shows "Following", appears on
  the phone; unfollow reverses; follow from the phone → pill + sidebar update inbound.

### Phase 5 — Revision-gated `/diff` + on-open SWR (RC5)
- `FetchPlaylistDiffAsync` + `FormatRevision` + 304/509/torn-apply tree (§2.6).
- `EnsureFetchedAsync` SWR split (blocking first-open / background revalidate) + the 5-min window.
- `PlaylistPush` gate 3 uses `/diff` for the open playlist.
- **Tests**: crafted diff applies; 304 no-ops; 509 falls back to full; revision wire-string formatting
  (`counter,hex` + `%2C`); reopen-after-external-edit refreshes; on-open with fresh revision costs one 304.
  **Live exit**: reopen a playlist edited elsewhere → updates without full reload; open an unchanged playlist
  → log shows `UpToDate`, no membership write.

### Phase 6 — Reconnect resync + hardening
- Full `ReconnectResync` (ordered steps + 30 s rate limit, §6.2); route post-write drains through the loop;
  optional ws-connect force-token-refresh (transport dossier G6); `--spotify-sync` rebased onto `LibrarySync`
  (§11); telemetry counters.
- **Tests**: simulated `Reconnecting→Online` transition runs drain→rootlist→deltas→open-playlist-diff in
  order; flapping transitions coalesce; interleaved `PlaylistPush` + `DrainWrites` for one uri produce a
  deterministic final state. **Live exit**: toggle airplane mode / switch Wi-Fi networks; make a change on the
  phone during the gap + a like in Wavee during the gap → within seconds of reconnect both sides converge; no
  duplicate applies (echo counters), no flicker.

### Phase 7 — Realtime membership choreography (§4.6)
- Spike first: per-slot transform channels on `ItemsView` bound rows vs the `BoundRowContent` transform-prop
  fallback (§4.6 engine-dependency check) — half a day, decides the mechanism.
- `MembershipDiff` (pure diff + reset classification), `TrackListChoreographer` (anchor, FLIP moves, staggered
  adds, ghost exits, reset crossfade), motion-token constants.
- Depends on Phase 1 (in-place refresh delivers the new list) and benefits from Phase 5 (diffs keep
  `RetainedFraction` high so incremental choreography, not reset, is the common case).
- **Tests**: `MembershipDiff` unit matrix — single move / add / remove / combined ops / full re-cut →
  correct `RowChange` sets + `IsReset` classification; anchor-offset math (add/remove above, at, below the
  viewport); stagger/ghost caps honored. Choreography itself is verified on-device (animations are visual):
  scripted `StubTransport.PushEvent` sequences against a live window — move a track (slides), add (eases in,
  staggered), remove (ghost fades, gap closes), editorial re-cut (crossfade), all with scroll position stable.
  **Live exit**: reorder/add/remove tracks from the phone with the playlist open in Wavee → each change
  narrates itself; a Discover-Weekly-style full refresh crossfades; reduced-motion honored end to end.

---

## 13. File/type change map (delta over the proposal's map)

**New**
- `Wavee/Backend/Sync/LibrarySync.cs` — loop + `SyncCommand` + counters + echo ring + dirty/lastRevalidated maps.
- `Wavee/Backend/SwitchableTransport.cs`.
- `Wavee/Backend/Collections/CollectionWriteMapper.cs` + `CollectionSets` (WireSet's new single owner).
- `RootlistFollowStrategy` + `MutationEngine.Follow` + `HasPending` (in `Mutation.cs`).
- `SpotifyHeaders.PlaylistV2Mutation` + zstd guard (in `Wavee/Backend/Spotify/`).
- `Wavee/Components/MembershipDiff.cs` — keyed row diff + reset classification (§4.6).
- `TrackListChoreographer` (in `Wavee/Features/Detail/`) — anchor / FLIP / stagger / ghost / reset motion (§4.6).

**Changed**
- `LiveSessionHost.cs` — go-live composition (§3), transport swap, connectivity subscription, retire the
  mosaic-tracklist half of `HydratePlaylistHeadersAsync`.
- `Services.cs` — `MutTransport`/`RealCold`/`RealMutations`/`RealSessionHost`; strategy registration;
  `GoOffline` stub-restore; real username into `SessionContextHost`.
- `Seam.cs` — follow routing, `SetForUri` + `AllSets` playlist entries.
- `Mutation.cs` — `SetReplayStrategy.Replay` rewrite; `OpRebaseStrategy` headers + response-revision capture;
  drain backoff scheduling.
- `PlaylistFetcher.cs` — `FetchPlaylistDiffAsync`, `FormatRevision`, rootlist revision pass-through.
- `PlaylistWireMapper.cs` — `BuildRootlistChanges` (info/nonces/want-flags/ItemAttributes).
- `CollectionFetcher.cs` — WireSet/UriPrefix delegated to `CollectionSets`; paging mark-and-sweep.
- `DealerRouter.cs` — enqueue-only, rootlist topic branch, payload pass-through for collections.
- `Store.cs` / `CachedStore.cs` / `SqliteColdStore.cs` — `SetRootlist(entries, rev)` + `RootlistRevision()`;
  `SetSaved` no-op elision; `meta["rootlist_rev"]` accessors.
- `StoreLibrarySource.cs` — `EnsureFetchedAsync` playlist SWR split + `Sync` hook property.
- `DetailPage.cs` / `DetailShell.cs` — in-place refresh effect + `SetOpenContext`.
- `DetailTracks.cs` — track-set view-cache invalidation.
- `WaveeSidebar.cs` — playlists via `LibraryStore.Playlists`.
- `LibraryStore.cs` — ensure a `Playlists` Warm cell exists for the sidebar (it already refreshes on
  `CollectionsChanged(Playlists)`).
- `Scaffold.cs` — register `RootlistFollowStrategy`.
- `SpotifyLibrarySync.cs` — rebase onto `LibrarySync` (Phase 6).

**Untouched by design**: `PlaylistDiffApplier`, `CollectionDeltaApplier` (applier semantics are correct),
`SaveButton`/`FollowButton`, `LibraryBridge` (already optimistic + post-marshalled), the engine (`fluent-gpu`)
— no engine changes anywhere in this plan.

---

## 14. Deliberate non-goals (v1)

- No OS network-change listener (dealer watchdog converges within ~70 s worst-case).
- No background TTL sweep over playlists (event-driven + on-open only — the anti-herd contract).
- No UI for pending/dead-letter sync state (the store's `SyncState` rows are the future hook).
- No playlist *edit* UX changes (OpRebase gets protocol fixes in passing; add/remove/reorder UI is out of scope).
- No `ylpin` (pinned) / `ban` / `enhanced` sets — the 5 existing logical sets only; the set list is one
  constant + one WireSet row away when wanted.
