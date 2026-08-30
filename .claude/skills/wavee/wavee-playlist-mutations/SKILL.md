---
name: wavee-playlist-mutations
description: Wavee's playlist/rootlist write path — the desktop-verified /changes wire, the dealer inbound gate trees, the durable outbox, and the playlist page's edit affordances. Read before changing any of them.
---

# Wavee playlist mutations (wire + sync)

The write half of the library: every playlist and rootlist edit Wavee sends, every playlist frame the dealer
sends back, and the invariants that keep the two from corrupting each other.

**Read this before touching:** `Backend/Playlists/**`, `Backend/Mutation.cs`, `Backend/Sync/LibrarySync.cs`
(playlist/rootlist/permission arms), `Backend/Realtime/DealerRouter.cs`, `IPlaylistMutationSource`
(`Wavee.Core/Sources/SeamPorts.cs:74`), or the detail page's edit/reorder/notice surfaces.

Every wire shape below is **desktop-verified** against two Fiddler captures + one dealer archive (2026-08-15)
and pinned by byte-exact goldens. Design record: `docs/plans/wavee/playlist-saz-fable5-handoff.md` (read its
**Errata** section first — the original body is wrong on several load-bearing points).

## 1. Outbound wire contracts

**Envelope** (`PlaylistWireMapper.Build`, `:214`) — `POST /playlist/v2/{path}/changes`, body = raw `ListChanges`
under a lying `Content-Type: application/x-www-form-urlencoded` (`SpotifyHeaders.PlaylistV2Mutation`):

| Part | Shape | Note |
|---|---|---|
| `ListChanges` | `base_revision`, one `Delta`, `want_resulting_revisions=1`, `want_sync_result=1`, `nonces=[n]` | exactly ONE nonce; desktop's is a per-list per-session counter, ours is random |
| `Delta` | `ops[]` + `info` | **no `base_version`** — the top-level base is the base |
| `ChangeInfo` | `{user, timestamp}` **only** | never admin/undo/merge/redo/source |
| sync-reason header | `CAk=`(9) edit · `CAw=`(12) create · `CAI=`(2) lens ADD | `spotify-apply-lenses: auto` on playlist, not rootlist |

**Ops** (`ToWireOp` `:252`):

- **ADD** — `attrs{timestamp, item_id}` per item; `add_first`/`add_last` emitted only when true, else `from_index`.
  `item_id`s are **client-minted** (`SpotifyIds.NewItemId`, 8 random bytes) and the server **keeps them**
  (verified: A046's 50 ids came back byte-identical on the next GET). `added_by` is never sent.
- **Keyed REM** — `Rem{items_as_key=1, items:[Item{uri, attrs{item_id}}]}`, **one op per row**, all in one Delta.
  No index at all. Index REM (`from,length`) is the fallback for rows whose id we never learned — all-or-nothing
  per batch (`PlaylistMutationSource.BuildRemoveOps` `:176`), because mixing shapes in one Delta shifts indices.
- **Keyed MOV** — `Mov{items[], add_after_item | add_first | add_last}`, **fields 1–3 absent**. Anchor derivation
  lives in `PlaylistMutationSource.BuildKeyedMove` `:218`: `toIndex<=0`→`add_first`; `>=Count`→`add_last`; else the
  row at `toIndex-1` **walking back over rows that are themselves moving** (that is what makes a gapped multi-select
  land as one contiguous run); nothing left → `add_first`. One op for the whole selection.
- **UPDATE_LIST** — `new_attributes` only. `collaborative=false` and `deleted_by_owner=false` travel as *values*,
  never also as `no_value` (the `no_value` form is parse-side only — it is what the dealer's `old_attributes` uses).
- **Rootlist ops are positional** — `Mov{from,length,to}`, index `Rem`, index `Add`. Playlist row ADD carries
  `attrs{ts, public=1}`; folder markers never carry `public` (`IsGroupMarker` `:359`).

**Create** (`BuildCreateChanges` `:180`) — `POST /playlist/v2/playlist/{clientMintedId}/changes` against the
**8-byte** base `00000000726f6f74` (`PlaylistRevisions.CreateBase`), one `UPDATE_LIST{name}` op, sync-reason 12.
The base is deliberately not 24 B, so the dealer's create echo (which carries the same 8 B as `parent_revision`)
just fails every equality gate. The response has **no name and no contents** — the store is seeded optimistically.

**Folder CRUD** (`RootlistOps`, pure builders `:279`–`:337`) — one Delta each:
- create: `ADD{from=i, start-group{ts}}` + `ADD{from=i+1, end-group{ts}}`; name is escaped into the marker uri.
- rename: `REM{from=start, len=1}` **with no items** + `ADD{from=start, start-group', attrs{ts = the marker's
  ORIGINAL create ts}}`. A resident row with no ts forces a bootstrap GET first (`RenameFolderAsync` `:546`).
- delete: `REM{end,1}` then `REM{start,1}` — children stay and belong to the enclosing level.
  **REFERENCE-INFERRED, not desktop-captured** (see §8).

**Permission** (`PlaylistPermissionClient`) — proto, not JSON, not revision-chained:
`GET /playlist-permission/v1/playlist/{id}/permission/base` → `Permission{revision(8 B or "default"), level}`
(404 = fresh playlist → null). `POST …/permission/base/level` body = `SetPermissionLevelRequest`, literally
`08 01` (BLOCKED) / `08 02` (VIEWER) → `SetPermissionResponse.resulting_permission`. 409 → one GET + one retry,
then `Conflict`; 403 → `Forbidden`. The client is **stateless** — the revision lives on the store header.
Contributor grants are JSON on **`Channel.SpclientWg`** (`permissionLevel` nested under `"permission"`).

**Response handling** — playlist `/changes` replies are zstd, rootlist single-delta replies plain: sniff magic
(`SpotifyZstd.MaybeDecompressZstd`). `multiple_heads(9)` / `changes_require_resync(20)` → resync, never advance.

## 2. Inbound (dealer) contracts — `DealerRouter`

| Topic | Payload as it really arrives | Handling |
|---|---|---|
| `hm://playlist/{v2/,}user/{u}/rootlist` | head-only **`PlaylistModificationInfo`** (uri + 24-B `new_revision`, no parent, no ops), delivered **twice** (v2 + non-v2) | `TryDecodeRootlistPush` `:146` sniffs PMI-vs-RMI by "is field 1 a `spotify:` uri"; 24-B gate; 4-slot `RecentHeadRing` dedups the pair → one enqueue per head |
| `hm://playlist/v2/playlist/{id}` | PMI: echo / ops / head-only / tombstone | `OnPlaylist` `:94`; head-only = "new head", not signal regeneration |
| tombstone | `UPDATE_LIST new{deleted_by_owner=1} old{no_value[6]}` | `CarriesTombstone` → `ApplyTombstone` |
| `hm://playlist-permission/v1/playlist/{id}/permission/state` | `PermissionStatePub` (carries **no uri** — it comes from the topic) | `OnPermission` `:53` → `PlaylistPermissionPush`; missing `base_permission` = logged drop |
| `hm://playlist/v2/list/liked-songs-artist/…` | non-playlist uri, no revision, no ops | logged drop `no-head-no-ops` |

The rootlist push is a **revision hint**, not a delta: it never carries ops, so it can only echo-drop or trigger a
GET. A real ops-carrying `RootlistModificationInfo` is still supported via the fallthrough.
Drop reasons: `unparseable`, `no-uri`, `not-a-spotify-uri`, `unsupported-op`, `no-head-no-ops`, `bad-revision:{len}`.
`DealerArchive.IsHandled` labels both `hm://playlist/` and `hm://playlist-permission/`.

## 3. The gate trees (exactly as in code)

`LibrarySync.RootlistPushAsync` `:323` — ordered, total, and unable to store a non-24-B head:
1. `!IsWellFormed(newRev)` → full GET (defensive; the router already drops these).
2. `Equal(stored, newRev)` → echo drop.
3. `ops.Count>0 && Equal(stored, parentRev)` → apply in place → `SetRootlist(entries, newRev)`; torn → full GET.
4. else → full GET (`head-only` / `parent-mismatch`). **An empty-ops push never calls the 2-arg `SetRootlist`.**

`LibrarySync.PlaylistPushAsync` `:523`:
1. `Equal(stored, newRev)` → echo drop.
2. tombstone → `ApplyTombstone` — deliberately **before** the pending gate (a deleted list cannot be converged).
3. **pending gate (I3a)**: `_mutations.PendingFor(uri) > 0` → `MarkDirty` only. No in-place apply, no revalidate.
4. new head (well-formed `newRev`, no usable parent, no ops) → open: `PlaylistRevalidateAsync`; cold: `MarkDirty`.
5. resident + `Equal(stored, parentRev)` → apply ops in place, hydrate only the added uris, header refetch if the
   batch carries UPDATE_LIST; adopt `newRev` only when well-formed.
6. else → open: revalidate; cold: mark dirty (anti-herd).

`LibrarySync.ApplyTombstone` `:618` — ONE bulk: rootlist row removed **revision-preservingly**, saved pill false,
membership emptied, header latched `DeletedByOwner=true`, `Bump(uri)` + `Bump("rootlist")`. Idempotent.

`LibrarySync.PermissionPushAsync` `:639` — cold header → ignored (`PermissionPushIgnored`, seeded on open by
`SeedPermissionAsync` `:662`, the ONE place a permission GET happens). Resident → writes `IsPublic`,
`BasePermissionRevision`, `Capabilities.IsCollaborative`, zero network.

`LibrarySync.HealRootlistRevision` `:378` — at sync start, before the drain: a persisted non-24-B rootlist revision
is cleared so hydrate's full GET rewrites SQLite meta.

`MutationEngine.AdoptSnapshot` `:649` / `LibrarySync.AdoptSnapshot` `:679` — the single membership-replace
chokepoint: I1-gate the revision, `SetMembership`, then `ReapplyPending(uri)` `:668` (still-pending ops re-applied
in id order on top; one that no longer fits dead-letters `Conflict`, it is never rolled back).

`OpRebaseStrategy.CaptureChangesResponse` `:249` — the I4 order:
(a) `multiple_heads || changes_require_resync` → `PlaylistResyncQueue.Mark`, revision **unchanged**;
(b) `sync_result` ops → apply to local membership **first**, then adopt `resulting_revisions[^1]` (torn → mark, no
advance); (c) full contents → replace + adopt; (d) rev-only (empty `sync_result`) → advance the revision only.
The queue is drained right after `Drain` on the sync loop (`LibrarySync` `:887`).

`MutationEngine.Drain` `:774` — **ordered per entity**: an op that did not land blocks every later op on the same
entity this pass (that is what keeps create → rootlist ADD → seed tracks in order). A
`PlaylistMutationException` from a strategy = terminal → rollback + dead-letter immediately, no attempt burn;
everything else is retryable with exponential backoff (`min(60s, 2^attempts)`), 10 attempts then dead-letter.
Terminal kinds are recorded per edit id and rethrown once by `PlaylistMutationSource.DrainAsync` `:470`
(still queued afterwards → `Offline` when the transport is the stub / there is no account, else `Pending`).

## 4. Invariants I1–I8

- **I1 revision well-formedness** — a stored playlist/rootlist revision is always 24 B (`PlaylistRevisions`).
  The 8-B create base and the 8-B/"default" permission revision never enter those slots; malformed persisted
  state self-heals to null → full GET.
- **I2 one writer per entity** — network membership/rootlist writes only on the `LibrarySync` loop; **every**
  rootlist write takes `RootlistLane` (direct ops in `PlaylistMutationSource`, and `RootlistFollowStrategy.Replay`
  takes it itself, inside the strategy).
- **I3 local intent wins until acked** — (a) pending > 0 → pushes mark dirty, never apply in place;
  (b) every snapshot replace ends in `ReapplyPending`; (c) dead-letter rollback restores the pre-edit snapshot.
- **I4 never advance a revision past ops you did not apply** — see `CaptureChangesResponse` above.
- **I5 index ops are base-bound** — `RebaseOps` `:183`: ADD `from_index` recomputed from its recorded
  `wavee_anchor_item_id` (anchor gone → append); index REM re-expressed as keyed when every row has an id, else
  **terminal `Conflict`**. Keyed ops pass through untouched. Rootlist structural ops are index ops by nature →
  **online-only** (`RequireOnline`), lane-serialized, 409 → rebuild-against-fresh ×2 → `Conflict`.
- **I6 echo suppression is by revision only** — stored == resulting rev → drop. Keyed ADD is additionally
  idempotent by `item_id`.
- **I7 every inbound frame is accounted for** — parse failure / bad head / unknown shape → logged drop with a
  reason (`PlaylistMutationDiagnostics.DealerDrop`, `RootlistPushDeduped`). Nothing is swallowed.
- **I8 reconnect closes gaps** — `ReconnectResync` revalidates rootlist + open/dirty playlists; pending drains first.

## 5. Operation × hazard matrix

| Op | Wire | Optimistic / ack / echo | Hazards handled |
|---|---|---|---|
| Add tracks | `ADD{add_last}` / `ADD{from_index}`, `attrs{ts, item_id}` | membership + minted ids inline; ack = rev (+`sync_result`) | insert rebase by anchor (I5); duplicates legal (independent ids); deleted/forbidden → terminal |
| Remove rows | N× keyed `REM` in one Delta | rows removed inline by id | duplicates disambiguated; row gone remotely → torn → refetch → reapply is a no-op; unkeyed rows → index fallback (I5) |
| Move rows | ONE keyed `MOV` (gapped selection included) | applied inline with the same op | anchor removed concurrently → torn → refetch → reapply; unkeyed row/anchor → `Pending` refusal, **never** a positional fallback; dealer echo arrives positional → dropped by rev / deferred by I3 |
| Update details / cover | `UPDATE_LIST{name\|description\|picture\|collaborative}` | header patch inline | header refetch when a push's ops contain UPDATE_LIST |
| Create playlist | create-`/changes` (8-B base) → rootlist `ADD{attrs{ts,public=1}}` → seeds | store row + empty membership + rootlist entry inline; ordered outbox ops | offline create queues; 4xx → terminal, rollback + the rest of the recipe dropped (no orphan row); echo parent is 8 B |
| Delete playlist | rootlist index `REM{from,len=1}` | rootlist entry + saved pill evicted | tombstone push also arrives (idempotent); pending edits → terminal `Deleted` |
| Follow / unfollow | rootlist `ADD` / keyed `REM` | pill + entry inline, rev-preserving | I2 lane; ADD index resolved from the folder at replay, not at enqueue |
| Rootlist reorder / folder CRUD | positional `MOV` / marker `ADD`+`REM` | tree computed **locally** (the reply has no contents) | 409 → rebuild ×2 → `Conflict`; online-only |
| Set permission | proto `POST …/base/level` | header `IsPublic` inline + best-effort rootlist `UPDATE_ITEM{public}` | 409 → GET + retry once; 403 → `Forbidden`; dealer `permission/state` converges |
| Contributor invite | JSON grant on `SpclientWg` | — | no dealer topic — the HTTP response is the only ack |
| Inbound rootlist / playlist / permission push | see §2 | — | §3 gate trees |

## 6. Item ids and revisions

`SpotifyIds` — `NewItemId()` 8 bytes → 16 hex, `NewPlaylistId()` 16 bytes → base62/22, `NewGroupId()` 8 bytes → hex.
`PlaylistRevisions.IsWellFormed` is the single gate; `Equal` treats two nulls as **not** equal (unknown must never
read as an echo). Every revision writer routes through it: `PlaylistFetcher.StorableRevision`,
`CaptureChangesResponse`, `MutationEngine.AdoptSnapshot`, both push handlers, `HealRootlistRevision`.

## 7. Seam + UI surface

`IPlaylistMutationSource` (`SeamPorts.cs:74`) as landed. Note the shapes that differ from the design doc:
rootlist moves are the **batch** `MoveRootlistItemsAsync(IReadOnlyList<RootlistMove>, ct)` with the single-item call as
its N=1 sugar (see *Batch moves* below);
`MoveRowsAsync(uri, rows, **int toIndex**, ct)` — the anchor is derived **in the backend** by `BuildKeyedMove`;
`CreatePlaylist(name, placement)` is **synchronous** and returns `PlaylistCreated(Uri, Completion)` (the optimistic
row is already in the store); folder CRUD is `CreateFolderAsync` / `RenameFolderAsync` / `DeleteFolderAsync`.
Stubs (`LocalPlaylistMutationSource`) implement the new members as named `NotSupportedException` throws.

**Failures**: everything that reaches UI is a `PlaylistMutationException{Kind}`
(`Unknown, Conflict, Forbidden, Deleted, Offline, Pending, NotSupported`). Classification is
`PlaylistEditErrorKinds.KindOf` (`Features/Detail/PlaylistEditErrorKinds.cs:26`); copy is `KeyFor(kind, verb)` `:45`
with `PlaylistEditVerb {Generic, Add, Remove, Reorder, Rename}`; `PlaylistEditErrors.Toast` renders it and
`IsInformational` (Offline/Pending) picks the severity. **There is no `ex.Message` fallthrough** — Unknown maps to
`detail.edit.failed`.

| Kind | Loc key | Reorder variant |
|---|---|---|
| Conflict | `detail.edit.conflict` | `detail.edit.reorderConflict` |
| Forbidden | `detail.edit.forbidden` | — |
| Deleted | `detail.edit.deletedElsewhere` | — |
| Offline | `detail.edit.queuedOffline` (info) | — |
| Pending | `detail.edit.pendingSync` (info) | `drag.stillSyncing` |
| NotSupported | `detail.edit.offlineSpotifyEdits` | — |
| Unknown | `detail.edit.failed` | — |

**Store header fields the UI reads** (`Wavee.Core/Domain/Models.cs:419`): `IsPublic`, `BasePermissionRevision`,
`Capabilities.IsCollaborative`, `DeletedByOwner`. The detail page never issues its own permission GET.

**Notice**: `DetailNotice {None, Deleted, AccessRevoked, CreateFailed}` (`PlaylistPageNoticeRules.cs:7`), decided by
the pure `PlaylistPageNoticeRules.Next(...)` / `.Cold(...)`, rendered by `PlaylistNoticeBar` in `DetailShell`, and
gating every edit affordance through `PlaylistInlineEdit.Editable`.

**Pending chip**: `MutationEngine.PendingChanged`/`PendingFor` → `LibraryBridge.PendingEdits(uri)` +
`PendingEditsTotal` → `PlaylistPendingChip` in the header and one line in `NotificationPanel`.

**Reorder gate** (`PlaylistReorderRules` + `PlaylistDropRefusalRules` in `Features/DragDrop/WaveeDragRules.cs:106`)
is **split**: the rows half refuses at the drop verdict (`PlaylistDropRefusal.Syncing`); the anchor half is checked
at commit in `DepositAtAsync`, the first moment a slot exists. `TryBlockMove` (keyboard) checks both up front.

### Rootlist moves — ONE legality authority, and one commit

**`RootlistOps.CheckMove(store.Rootlist(), source, target, placement)` is the only decider in the app.** It runs over
the **flat marker stream** (`RootlistEntry[]` — a folder is a balanced `start-group` / `end-group` pair, kind 1 / 2),
which is the very structure the write indexes into, and it is literally `TryBuildMove(…, out RootlistMoveCheck)` with
the op discarded — so "the cue says yes" and "the writer can build it" cannot differ.

| | |
|---|---|
| lives in | `Backend/Playlists/RootlistOps.cs` (backend, engine-free) |
| decides over | the flat marker stream — `IStore.Rootlist()` |
| answers with | `RootlistMoveCheck {Ok, Missing, SameItem, Invalid, NoOp, Cycle}` |
| batch form | `RootlistOps.CheckMoves(entries, IReadOnlyList<RootlistMove>)` — the same enum for a whole multi-selection |
| reached by | **exactly one call site**: `RootlistDropDecision.Check` (`Features/Sidebar/Data/RootlistDropDecision.cs`) |
| reached from | `LibraryBridge.CheckRootlistMove` (the app-side accessor, over the attached `IStore`) · the sidebar drop cue · the rail folder tile · `RootlistTreeNav` (the "Move to folder…" picker + the menu's `HasDestinations`) |
| UI mapping | ONE table, `RootlistDropDecision.RefusalFor`: `Ok→None`, `NoOp→NoOp`, `Cycle→IntoDescendant`, `SameItem→Self`, `Missing`/`Invalid`→`Unavailable` |

**There is no second checker.** `RootlistTreeMoves.Check`/`TryRange` — a copy of the index math over the *flattened
projection tree* — was **deleted on 2026-08-16**, along with the pane's own `SourceContainsRow` cycle walk. It could not
tell "after the folder" from "after the folder's last child" (a tree row list has no end marker between them), so it
called legal moves `NoOp`: the user was told "Already there" for a perfectly good filing, and the picker silently hid
legal destinations. `RootlistDropScenarioTests.NoRefusalIsEverComputedFromAMarkerFreeTree` is the guard — it scans app
CODE (comments stripped) for the deleted names and asserts `RootlistOps.CheckMove` still has exactly one call site.

**A refused move is a THROW, not a quiet return.** `PlaylistMutationSource.MoveRootlistItemAsync` used to `return` when
`TryBuildMove` refused, so the awaiting caller completed successfully and posted "Moved to …" for a move that was never
sent. It now throws a typed `PlaylistMutationException`: `NoOp`/`SameItem` → `PlaylistMutationFailure.NoOp` ("Already
there", `drag.alreadyThere` for `PlaylistEditVerb.Reorder`), `Cycle`/`Invalid` → `PlaylistMutationFailure.Invalid`
("Can't move here", `drag.cantMoveHere`), anything else → `Conflict`. Both new kinds are **informational**, not errors
— nothing was lost. Pinned by `RootlistMoveSeamTests` (which also pins *one legal move ⇒ exactly one POST*).

### Batch moves — ONE seam, ONE delta

A multi-selection drop is one gesture, so it is one write:
`IPlaylistMutationSource.MoveRootlistItemsAsync(IReadOnlyList<RootlistMove> moves, ct)`, where
`RootlistMove = (RootlistItemRef Source, RootlistItemRef Target, RootlistDropPlacement Placement)`.
`MoveRootlistItemAsync(source, target, placement)` is the **N=1 sugar over it** — literally
`MoveRootlistItemsAsync([new(source, target, placement)])`. There is no second write path, so nothing can drift.

| | |
|---|---|
| builder | `RootlistOps.TryBuildMoves(entries, moves, out ops, out reason)` — the ops of ONE `Delta` |
| writer | `PlaylistMutationSource.MoveRootlistItemsAsync` → the shared `RunRootlistOpAsync(logKey, build, ct)` loop folder CRUD uses (build → apply locally → POST → adopt, ONE 409 rebase, ONE rollback of `before`) |
| checker | `RootlistOps.CheckMoves` — `TryBuildMoves` with the ops discarded (the drop cue's authority, so cue and writer cannot disagree) |
| bridge | `LibraryBridge.MoveRootlistItemsAsync` |

**Sequential index math.** Each move is built with `TryBuildMove` against the CURRENT marker stream and then
`ApplyLocally`'d before the next one is built — because that is exactly how the server applies the ops of one Delta
("each against the state left by the preceding ones", `RootlistOps.ApplyLocally`). Building all N against the original
stream would post indices that are already stale by the second op. Multi-op rootlist deltas are the shape
`BuildCreateFolder` (2 ADDs) already ships, and the sequential build lives **inside** the `build(entries)` delegate, so
the 409 rebase attempt re-derives **every** op against the freshly bootstrapped stream.

**Three batch-only rules** (all of them "a batch is not N separate drops"):

- **the gather** — a source that IS the target is dropped from the batch up front. Dropping a multi-selection right
  after one of its own members is legal: the anchor stays, the others close up around it. Only when EVERY move is such
  a self-pair is the batch `SameItem`, which is what keeps the N=1 "onto itself" answer unchanged.
- **a per-move `NoOp` is skipped** (a member already sitting where the batch is headed must not veto the members that
  are really moving), while `Cycle` / `Missing` / `Invalid` on ANY move **refuses the whole batch** with that reason —
  half a filing is worse than none.
- **net-identity** — two real ops can cancel out (`{B,C} Before D` on `[A,B,C,D,E]`). Per-op checks cannot see that, so
  the FINAL uri stream is compared to the input: equal ⇒ `NoOp`, nothing posted.

**Submission order is the caller's.** Which order a same-target batch is handed over lives in the UI
(`RootlistBatchOrder.For`): `Before(t)` / `Inside(folder)` iterate the sources in tree order, `After(t)` / `EndOfList`
in **reverse** tree order, so each op lands adjacent to the anchor and the selection keeps its own order. The seam
executes the list it is given, in the order it is given. Worked examples (pinned in `RootlistFolderOpsTests`, through
`dest = to > from ? to - Length : to`): `[A,B,C,D,E]` move `{B,D}` After E in reverse ⇒ `[A,C,E,B,D]` (forward would
give `[A,C,E,D,B]`); Before A in tree order ⇒ `[B,D,A,C,E]`; Inside F in tree order appends before the end marker.

A refused batch is a **throw**, same as N=1 (`MoveRefused(reason)`); `RootlistMoveSeamTests` pins one delta with one
MOV per item, the rollback of the whole tree on a transport fault, and the 409 re-derivation of every op.

**One chokepoint.** Every rootlist move in the app**One chokepoint.** Every rootlist move in the app — a sidebar drop, a collapsed-rail folder drop, the row menu's
Move up / Move down / **Move to folder…** / Move out of, and Alt+↑/↓ — resolves its `(RootlistItemRef, placement)`
and then calls **`WaveeResourceDrop.MoveRootlist` → `LibraryBridge.MoveRootlistItemAsync`**; the four non-mouse
verbs funnel through `FolderActions.Commit` first. There is no second mutation path, which is why every one of them
awaits the seam, maps a failure by verb (`PlaylistEditVerb.Reorder`, never `ex.Message`), announces through
`Announcer`, and shows a `drag.movedTo` / `drag.movedToLibrary` toast.

**Undo anchors.** `RootlistUndoAnchors.TryResolve(tree, entryId, out anchor, out placement)` is captured **before**
the mutation — once the rootlist has moved, where the item used to be is unknowable. It resolves to the previous
sibling (`After`), else the next sibling (`Before`), else the parent folder (`Inside`), and returns `false` for the
tree's only top-level member; the toast then appears **without** an Undo rather than with one that would land
somewhere else. The inverse is an ordinary `MoveRootlistItemAsync` on the same seam.

UI contract for the drop cue itself (resolver, geometry, refusal captions, the mid-drag freeze):
`.claude/skills/wavee-sidebar/architecture.md` § *Rootlist drag & drop*.

## 8. Test infra

**Dealer replay** — `Wavee.Tests/Backend/DealerArchiveReplay.cs` loads `Fixtures/dealer/playlist-20260815.{idx.ndjson,bin}`
(a byte-exact `hm://playlist*` subset of a real session, re-offset into its own `.bin`), runs the rows through the
**real** `DealerFrameParser`, and pushes them into a real `DealerRouter` + `LibrarySync` over a `StubTransport` with
a scripted server. `DealerReplayTests` pins: 75 frames / 36 rootlist / 18 distinct heads each delivered twice;
rootlist revision is 24 B after every frame; ≤18 GETs and `RootlistPushDeduped == 18`; a head-only playlist push
does not full-refresh a cold list; the P3 tombstone evicts it everywhere and latches the header; the two permission
pushes flip BLOCKED→VIEWER with **zero** GETs.

**SAZ goldens** — `Wavee.Tests/Fixtures/playlist-wire/*.bin`, manifest + sizes in `WireGoldenFixtures.cs`.
Each is the raw HTTP **body** (everything after the first `\r\n\r\n`, `Content-Length` verified at extraction,
zstd-decompressed where the capture was compressed); no headers, no bearer token — `WireGoldenTests` asserts that.

| Golden | What | Byte-exact rebuild? |
|---|---|---|
| `a031-create-p1` | create via `/changes`, 8-B base | yes (`PlaylistCreateTests`) |
| `a042-rootlist-add-p1` | rootlist ADD, `attrs{ts, public}` | yes |
| `a046-add-50-tracks` | ADD `add_last`, 50 minted item_ids | yes |
| `a143-keyed-rem-x3` | one Delta, three keyed REMs | yes |
| `a148 / a154 / a498` | keyed MOV: after_item / add_first / add_last | yes |
| `a164-folder-create` | two marker ADDs, one Delta, no `public` | yes |
| `a281-rootlist-index-rem` | delete playlist: index REM | yes |
| `b037 / b128` | folder rename (inner / outer), original ts | yes |
| `b049-rootlist-mov` | positional `MOV{from,len,to}` | yes |
| `b063-update-list-name` | UPDATE_LIST name | yes |
| `b078 / b108` | `SetPermissionLevelRequest` = `08 01` / `08 02` | yes (`PlaylistPermissionTests`) |
| `a178-create-response` | create reply: rev bookkeeping, no name/contents | parse-only |
| `a164-folder-create-response` | rootlist reply, uncompressed, no contents | parse-only |
| `perm-get-blocked` | `Permission{revision(8 B), BLOCKED}` | parse-only |

**Regenerating**: extract the `.saz` (a zip) → `raw/{prefix}_c.txt` (request) / `_s.txt` (response), take everything
after the first `\r\n\r\n`, verify against `Content-Length`, decompress by **magic** (not `Content-Encoding`), and
write the body alone. Then update `RequestSizes`/`ResponseSizes`.

> **The rule**: a wire change must keep the goldens byte-exact, or come with a new capture. A rebuild assertion that
> "just needs updating" means Wavee no longer sends what desktop sends.

## 9. Known gaps / not captured

- `Mov.add_before_item` (field 5) is declared but **never observed**; `MapOps` rejects it as `unsupported-op`
  rather than reinterpreting it (there is no way to resolve "before X" without the list).
- **Folder delete is reference-inferred**, not desktop-captured — taken from the WaveeMusic `RootlistService`
  (remove both markers, children stay) as the exact inverse of the captured create. Flagged in `BuildDeleteFolder`.
- XM extension kind **225** (playlist metadata, what desktop uses) is unused; Wavee still reads 205.
- `Capabilities` in our proto is a **6-field prefix** of the ~20-field message desktop receives. Unknown fields
  round-trip; nothing reads the missing ones.
- `PlaylistModificationInfo` f5/f6/f8/f9 are observed but undeclared (f5 `{unix-s, nanos}`, f6 a host string).
- **`DeletedByOwner` is not persisted to SQLite** — no column in the v8 schema. It is relearned from the header on
  the next full GET (`PlaylistFetcher.HeaderOf`), and merged forward in-process by `Store.cs:256`.
- `DeletePlaylistAsync` still throws a raw `InvalidOperationException` when the uri is not in the rootlist —
  the one write path that has not been converted to a typed kind.
- The **P3 UI** (create flow, folder-row menu, folder loc keys) is landing separately: the seam, the wire builders,
  the goldens and the tests exist; the sidebar call sites and `sidebar.*folder*` strings may not yet. Rename/new-folder
  reuse the existing `ContainerActions.RenamePlaylist` ContentDialog rather than an inline sidebar editor (sidebar rows
  are recycled `ItemsView` slots that re-plan on every rootlist push and would lose focus mid-push).
