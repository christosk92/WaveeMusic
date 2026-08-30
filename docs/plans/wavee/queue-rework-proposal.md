# Queue system rework — canonical design (backend + UI)

Status: **IN PROGRESS** — backend + tests landed; UI motion/reconciliation rework landed in QueuePanel (2026-07-08).
Date: 2026-07-08. Supersedes the ad-hoc skip_to work from the same session (partially reverted in
`QueuePanel.cs`; the `DetailTracks.StartVisible` three-way branch survives but its controller
semantics change per §7.3).

Grounding evidence:
- Live repro log (clicked IU track → "FANCY" played; `queue.snapshot` `current=` ≠ NowPlaying row).
- Full cluster capture of a real complex session (album context + reordered user queue + jam patch
  row + autoplay station tail + delimiter): pasted 2026-07-08, referred to below as **FIXTURE-A**.
  Stored at `Wavee.Tests/Fixtures/cluster-complex-queue.json`.

> All fixtures are already decoded and stored under `Wavee.Tests/Fixtures/` (2026-07-08).
> ✅ **Sanitized** (2026-08-26): `public_ip` → `2001:db8::1`, the username (`queued_by`) →
> `31testuser000000000000000000`, every device id → a synthetic `deadbeef…` hex of the same length
> (distinct ids stay distinct), and the captured device name → `Test Laptop`. Structure, field lengths
> and row identity are unchanged, so the golden assertions still hold.
- Fiddler capture `C:\Users\ChristosKarapasias\Documents\Fiddler2\Captures\que.saz` (**FIXTURE-B**):
  what the official desktop client sends when clicking a queue row targeting another device.
  Decoded copies for tests: `Wavee.Tests/Fixtures/next-track-queue-row.json` (uid `q2`) and
  `next-track-autoplay-row.json` (uid `8e7f7ecb4ffb81fa`).
- Fiddler capture `C:\Users\ChristosKarapasias\Documents\Fiddler2\Captures\qqq.saz` (**FIXTURE-C**):
  `next_track` + `add_to_queue` + two large `set_queue` snapshots (queue growth 52 → 375 rows).
  Decoded copies for tests: `Wavee.Tests/Fixtures/add-to-queue-remote.json`,
  `set-queue-52.json`, `set-queue-375.json`.

---

## 0. Decisions locked (user, 2026-07-08)

| Decision | Choice |
|---|---|
| Queue-row click semantics | **Skip-in-place** within the live session (Spotify parity); never rebuild the session from the visible snapshot |
| User queue on jump | **Kept** — still drains first after the jumped-to track |
| History-row click | **Cursor-back** in session (entry becomes now-playing; upcoming re-derived; user queue still drains first) |
| Remote-device queue click | Match FIXTURE-B: forward **`next_track` with the full target row** (`track{uri,uid,metadata}`), uid-first identity — NOT `play`+`skip_to` |
| Identity | **Full session model**: stable per-item ids minted at insertion; one atomic snapshot carrying revision + current + rows; one id scheme for local and viewer |
| Startup recovery | **Full session restore** from the cluster (context + cursor + user queue + autoplay tail + options), shown paused, resumable instantly |
| Drag-to-reorder | **Design for it now** (ids + `MoveUserItem` op), ship the drag UI in a follow-up — **shipped**: `QueuePanel.cs` mounts a `Reorderable` over the panel body |
| Landing | **One rework branch** (no interim hotfix stage) |

---

## 1. Verdict — the broken business rule

The current system models *"click a row in the queue"* as *"destroy the playback session and
start a new one, with heuristic hints to find where you were."* Every consequence the user has
observed follows from that one wrong rule:

- `QueuePanel.PlayQueueEntry` (`QueuePanel.cs:264`) re-embeds the **visible snapshot** (≤1+50 rows)
  as a brand-new context via `PlayOrderedAsync` → `QueueCore.SetContext` (`Playback.cs:34`) then
  **clears the user queue and history** and truncates a long context to the on-screen window
  (embedded pages carry no `next_page_url`).
- The natural-order detail path (`DetailTracks.cs:922`) and the (reverted) queue skip path resolve
  identity across **two different orderings**: `ContextResolve.FindStartIndex`
  (`ContextResolver.cs:91`) falls back to a **blind index** into a freshly server-resolved list.
  For dynamic contexts (Daily Mix regenerates; `MaxEagerPages=8` truncates large playlists) the
  index lands on an unrelated track. That is the IU→FANCY bug.
- Three notions of "current" (`QueueCore._cur`, projection `_track`, bridge signal) publish at
  different times: `PushQueue` fires **before** `Emit` updates `_track`
  (`PlaybackController.cs:873` vs `:875`), so one frame's snapshot self-contradicts (seen in the
  log). The cluster fold also always overwrites `_track` (`PlaybackProjection.cs:220`) even inside
  the local-command suppression window that protects play-state and volume.
- Entry identity is positional and dual-scheme: local `now/q{i}/c{absIdx}/h{i}`
  (`Playback.cs:231-252`) vs viewer `n{i}/h{i}` (`PlaybackProjection.cs:425-450`). Ids change
  meaning on any mutation; the UI papers over it with a whole-list remount keyed on a content hash
  (`QueuePanel.cs:93-110`).

The fix is the inverse rule: **the session is the durable object; user actions are cursor moves
and edits within it.** Session teardown happens only when the user explicitly starts a new context
(detail-page play, transfer-in of a different context).

## 2. Fault inventory (all verified against code this session)

| # | Fault | Where |
|---|---|---|
| F1 | Queue click rebuilds session from visible window; user queue + history destroyed; context truncated to ≤51 rows; queue-provenance rows relabeled `context` | `QueuePanel.cs:264-299`, `Playback.cs:34-46` |
| F2 | skip_to blind-index fallback across divergent orderings (dynamic contexts, >8-page playlists); `IsNaturalContextOrder` proves view==UI list, not view==server list | `ContextResolver.cs:91-99`, `LiveContextResolver.cs:33,81`, `DetailTracks.cs:947` |
| F3 | Split publish: `PushQueue` before `Emit`; `SetLocalContext` sets context but not track → snapshot `current` ≠ NowPlaying row for a frame | `PlaybackController.cs:662,873,875`, `PlaybackProjection.cs:171-178,314-326` |
| F4 | Cluster fold always overwrites `_track`, even when we're active inside the 2500 ms local-command window (only play-state/volume are guarded) | `PlaybackProjection.cs:217-235` |
| F5 | Positional dual-scheme EntryIds (`c{abs}` local vs `n{i}` viewer); UI remount-hash hack | `Playback.cs:231-252`, `PlaybackProjection.cs:425-450`, `QueuePanel.cs:93-110` |
| F6 | Dead ternary `CurrentUid.Length > 0 ? "context" : "context"` — emitted event's provider distinction lost | `PlaybackController.cs:807` |
| F7 | Inbound `next_track` endpoint **unhandled** (→ `Unknown`, dropped); inbound `skip_next` discards the track payload → official-app queue clicks against us do nothing / advance one | `ConnectCommand.cs:76`, `PlaybackController.cs:543` |
| F8 | Inbound `set_queue` keeps only `provider=="queue"` rows; context/autoplay rows in `next_tracks` are dropped instead of reconciled | `PlaybackController.cs:1414-1434` (ParseQueueTracks), `:594` |
| F9 | No startup session recovery: ghost-resume restores current track only; queue/user-queue/autoplay tail lost across restarts | `PlaybackController.cs:751` (GhostResumeAsync) |
| F10 | Autoplay tail not modeled as its own context (`spotify:station:...`); delimiter/`meta:page` rows and `advancing_past_track:"pause"` semantics absent | `Playback.cs`, FIXTURE-A |
| F11 | Cap disagreement: NextUpCap 50 / snapshot history 16 / retention 32 / viewer prev 16 | `Playback.cs:233,185,245`, `PlaybackProjection.cs:440` |
| F12 | Dead UI: `QueueButton` flyout (unmounted) plays single tracks order-destructively | `PlayerBar.cs:963-1012` |

## 3. New protocol facts this design is built on

From **FIXTURE-A** (real cluster):
- The now-playing track itself can be `provider:"queue"`, `uid:"q0"` — "current" is not always a
  context row.
- User-queue uids are minted in ADD order (`q0..q5`) but the wire order is the user's REORDERED
  order (`q3,q1,q4,q5,q2`): **uid is identity, position is presentation.**
- `next_tracks` composition: user-queue block → context continuation (context rows carry real uids
  even for albums) → patched rows (`jam.patch_track`, provider `context` w/ metadata provider
  `queue`) → autoplay tail with `provider:"autoplay"`, its own `context_uri`
  (`spotify:station:album:...`), `autoplay.is_autoplay:"true"`, `decision_id` → hidden
  `spotify:meta:page:1` and `spotify:delimiter` rows (`hidden:"true"`,
  `actions.advancing_past_track:"pause"`, `removed:["context/delimiter"]`).
- `queue_revision` can exceed Int64 (`12613583692104578720`) — keep string, parse ulong at echo.
- `context_metadata.context_patched:"true"` exists — contexts get patched server-side.

From **FIXTURE-B** (que.saz):
- Clicking a queue row targeting another device sends
  `{"command":{"endpoint":"next_track","options":{override_restrictions:false, only_for_local_device:false, system_initiated:false},"track":{uri,uid,metadata,album_uri,artist_uri},"logging_params":{...}},"connection_type":"wlan","intent_id":...}`
  gzipped with `X-Transfer-Encoding: gzip`. Works identically for user-queue rows (`uid:"q2"`) and
  autoplay rows (16-hex uid). **No `play`, no `skip_to`, no embedded pages.**
- Symmetrically, the official client will send US `next_track` when we're active — F7 must be
  fixed for parity in both directions.

From **FIXTURE-C** (qqq.saz):
- **The ACTIVE device mints q-uids, not the sender.** `add_to_queue` targeting another device
  sends `track:{uri, uid:"", metadata:{}}`. The first `set_queue` (52 queue rows) carries the
  pre-existing rows with their uids (`q2`,`q3`) and the newly-inserted rows with `uid:""`; the
  later snapshot (375 queue rows) shows those same uris now carrying active-minted `q4,q5,q6,…`.
- **Queues get huge**: 375 user-queue rows + 48 autoplay rows + markers in one 551 KB (pre-gzip)
  `set_queue` body, `prev_tracks=8`. No cap may exist in the model or the wire path; display
  windowing is a UI concern only.
- `set_queue`'s `next_tracks` includes the **autoplay tail and the `meta:page`/`delimiter` marker
  rows echoed through** — a set_queue rewrite must preserve them, not strip them.
- Queue rows on the wire carry top-level `album_uri`/`artist_uri` next to `metadata` (same shape
  as `next_track`'s track), plus `removed`/`blocked`/`restrictions` blocks.
- `queue_revision` differs per snapshot (server-minted per queue state); the client echoes the
  latest cluster value.

---

## 4. Target model — `PlaybackSession`

One pure, single-threaded class (successor of `QueueCore`; lives in
`Wavee/Backend/PlaybackSession.cs`, `Playback.cs` keeps only the `IPlayback` surface). All state
transitions return the atomic snapshot.

```csharp
// Wavee.Core/Domain/Models.cs — additions
public readonly record struct QueueItemId(ulong Value)
{
    public bool IsNone => Value == 0;
    public static readonly QueueItemId None = default;
}
public enum QueueProvider : byte { Context, Queue, Autoplay }
// QueueRowKind { Playable, Delimiter, PageMarker } already exists (PlaybackIds.cs:52)

// QueueEntry gains the stable id + provider enum + kind; EntryId string stays for
// wire/diagnostics but is now DERIVED ("i{ItemId}") and never parsed for position.
public sealed record QueueEntry(
    QueueItemId ItemId, string EntryId, Track Track, QueueBucket Bucket,
    QueueProvider Provider, bool IsAutoplay, string Uid = "",
    IReadOnlyDictionary<string, string>? Metadata = null);
```

```csharp
// Wavee/Backend/PlaybackSession.cs (new)
sealed record SessionItem(
    QueueItemId Id, Track Track, string Uid, QueueProvider Provider,
    QueueRowKind Kind, string? SourceContextUri,
    IReadOnlyDictionary<string, string>? Metadata);

public sealed record QueueSnapshot(
    long Revision,                       // local monotonic; bumped on EVERY mutation
    string? ContextUri,
    string? AutoplayContextUri,          // e.g. spotify:station:album:{id} once the tail exists
    QueueEntry? Current,                 // THE single source of "current"
    ImmutableArray<QueueEntry> History,  // actually-played stack, newest last
    ImmutableArray<QueueEntry> UserQueue,
    ImmutableArray<QueueEntry> Upcoming, // context + autoplay rows after cursor (providers mark them)
    bool Shuffle, RepeatMode Repeat,
    string ClusterQueueRevision);        // echo-through for outbound set_queue

public sealed class PlaybackSession
{
    // state: List<SessionItem> _context (play order), List<SessionItem> _naturalOrder,
    //        List<SessionItem> _userQueue, List<SessionItem> _history (cap HistoryCap),
    //        int _cursor, SessionItem? _current, ulong _nextItemId = 1, int _nextQueueUid,
    //        ulong _seedState, long _revision, string _clusterRevision = "";

    // — session lifecycle —
    public QueueSnapshot SetContext(string uri, IReadOnlyList<QueuedTrack> tracks,
                                    int startIndex, bool keepUserQueue = true);
    public QueueSnapshot RelabelContext(string uri);
    public QueueSnapshot AppendContextPage(IReadOnlyList<QueuedTrack> tracks,
                                           QueueProvider provider, string? sourceContextUri);

    // — cursor moves (THE new core ops; skip-in-place, session survives) —
    public QueueSnapshot? SkipToItem(QueueItemId id);   // Upcoming, UserQueue, or History target
    public QueueSnapshot? SkipToUid(string uid, string? uriFallback); // inbound next_track / remote clicks
    public QueueSnapshot? Next();                        // user queue drains first; repeat-aware
    public QueueSnapshot? Prev();

    // — user-queue edits —
    public QueueSnapshot EnqueueUser(IReadOnlyList<QueuedTrack> tracks);      // uid = "q{n++}"
    public QueueSnapshot EnqueueNextUser(IReadOnlyList<QueuedTrack> tracks);  // front-insert, same uids
    public QueueSnapshot? RemoveItem(QueueItemId id);    // user-queue or upcoming-context rows
    public QueueSnapshot? MoveUserItem(QueueItemId id, int newPos); // reorder (UI ships later)

    // — options —
    public QueueSnapshot SetShuffle(bool on);            // anchored Fisher-Yates over remaining context
    public QueueSnapshot SetRepeat(RepeatMode mode);

    // — inbound/recovery —
    public QueueSnapshot ReplaceFromCluster(ClusterDelta c, Track? hydratedCurrent);
    public QueueSnapshot ApplySetQueue(IReadOnlyList<QueueWireEntry> prev,
                                       IReadOnlyList<QueueWireEntry> next, string revision);

    public QueueSnapshot Snapshot();                     // the ONLY read
}
```

Semantics that must hold (each is a unit test, §12):

1. **Ids are minted once, at insertion**, from `_nextItemId++`. Never index-derived, never reused,
   survive reorder/remove/continuation-append. `EntryId = "i" + id` for diagnostics.
2. **User-queue uids are `q{n}`** minted at add time (matches the official scheme in FIXTURE-A/B),
   independent of display position, stable across reorder.
3. `SkipToItem` on an **Upcoming context/autoplay row**: previous current pushes onto History;
   cursor jumps to the row; the rows skipped over simply leave Upcoming (they do NOT enter
   History — history is *actually played*, matching Spotify). User queue untouched.
4. `SkipToItem` on a **UserQueue row**: that row becomes current (provider `Queue`); user-queue
   rows *before* it are consumed/dropped (drain semantics — same as pressing next repeatedly);
   rows after it remain queued. Context cursor does not move.
5. `SkipToItem` on a **History row** (cursor-back): the entry becomes current; History truncates
   above it; Upcoming re-derives from its position (for a context row, cursor = its context slot;
   for a played queue/autoplay row, it replays as a one-shot current with the context cursor
   unchanged). User queue still drains first.
6. `Next()` order: user queue head → context row after cursor → autoplay tail → (repeat modes) →
   session end. A `Delimiter` row with `advancing_past_track:"pause"` metadata **stops advance**
   when autoplay is off (F10); `PageMarker`/`Delimiter` rows are never surfaced as entries.
7. `SetContext(..., keepUserQueue: true)` is the default everywhere except explicit user-initiated
   new-context play (detail page play, inbound play command), which passes `false` — Spotify
   parity: starting a new context clears your queue? It does NOT — the official client keeps the
   user queue across context changes (FIXTURE-A: queue items from album A persist while station
   context plays). **Default `true` universally; `false` only for transfer-in.**
8. One `Snapshot()` shape serves local *and* viewer: the viewer path builds `SessionItem`s from
   cluster rows (ids minted the same way), killing the `n{i}` scheme (F5).
9. Caps: `HistoryCap = 32` retained, display windows applied at the bridge, never in the core.
   The core holds the **full** resolved context (paging appends), not a 50-row window (F1, F11).

## 5. Atomic publish — one pipeline, one truth

Every mutation follows exactly one path (F3, F4, F6):

```
PlaybackSession op → QueueSnapshot snap
    → NowPlayingProjection.ApplyLocalSnapshot(snap, PlaybackEvent? ev)   // ONE lock, ONE FireChanges
        - _track       = snap.Current?.Track     (never set anywhere else while we're active)
        - _queue       = window(snap)             (display windowing lives here: history tail 16, next 50)
        - _contextUri  = snap.ContextUri
        - _revision    = snap.Revision
    → bridge push-state (unchanged mechanics, now can't self-contradict)
```

- `SetLocalContext` + `SetLocalQueue` + the `Emit`-sets-`_track` path are **deleted**;
  `BuildEvent` takes the snapshot and reads `snap.Current.Provider` (F6 dead ternary gone).
- Cluster folds while we're active: `_track` joins play-state/volume under the local-command
  suppression guard (F4). Viewer mode: cluster wins for everything, unchanged.
- `PlaybackBucketDiagnostics.QueueIfChanged` logs gain `rev=` and `itemId=` columns; a DEBUG
  assert fires if `Queue[NowPlaying].Track.Uri != CurrentTrack.Uri` in any published state —
  the log contradiction becomes structurally impossible, and the assert proves it.

## 6. Controller API and routing

`IPlayback` (Wavee.Core/Playback/Playback.cs) — additions/changes:

```csharp
Task SkipToQueueItemAsync(QueueItemId id, CancellationToken ct = default); // NEW: the queue-panel verb
// PlayOrderedAsync: UNCHANGED signature; now detail-page-only (sorted/filtered views)
// PlayContextTrackAsync: UNCHANGED signature; fallbackIndex becomes a DIAGNOSTIC hint only (§7.3)
// MoveQueueAsync(entryId,…) → MoveQueueItemAsync(QueueItemId, int) (UI later)
// RemoveFromQueueAsync(entryId) → RemoveQueueItemAsync(QueueItemId)
```

Routing table for `SkipToQueueItemAsync`:

| We are | Action |
|---|---|
| Active device | `session.SkipToItem(id)` → if the new current differs: `LoadAndPlayCurrentAsync` (existing fast-start path) → `ApplyLocalSnapshot` |
| Viewer | Look up the row in the viewer snapshot → `OutboundEnvelope.NextTrack(row)` → POST to active device (FIXTURE-B shape). Optimistic: none (cluster echo updates us). |
| Idle (no session) | No-op (button shouldn't render) |

Inbound (F7): `ConnectCommand.cs` maps **both** `"skip_next"` and `"next_track"` to
`ConnectCmd.SkipNext` and parses an optional `command.track{uri,uid}` payload into the command
record. `HandleRemoteCommand`: payload present → `session.SkipToUid(uid, uri)` + play; absent →
`Next()` as today. (`skip_prev` never carries a payload; unchanged.)

Inbound `set_queue` (F8): `ApplySetQueue` reconciles ALL of `next_tracks` — queue rows replace the
user queue (by uid), context rows reconcile Upcoming by uid (heal ordering), autoplay rows
replace the autoplay tail; delimiter/meta rows stored as non-surfaced `Kind` rows.

## 7. Play-path corrections (detail pages)

### 7.1 Sorted/filtered view — unchanged
`PlayOrderedAsync(uri, VisibleOrder(v), displayPos)` stays: embedded pages are the correct wire
shape (matches the desktop captures) and are index-consistent by construction.

### 7.2 Collections — unchanged
URI-only `PlayAsync(uri, orig)`; sort rides on `context.url` server-side.

### 7.3 Natural-order view — identity-strict skip (F2)
`ContextResolve.FindStartIndex` loses the blind index fallback:

```csharp
// uid → uri → NOT index. Returns -1 on miss; fallbackIndex is only logged.
public static int FindStartIndex(IReadOnlyList<QueuedTrack> tracks, string? uri, string? uid);
```

`LocalPlaySpecAsync` on identity miss:
1. If the context has more pages (`next_page_url`), keep paging past `MaxEagerPages` (bounded,
   e.g. 40 pages) *only while searching for the skip target*, hydrating lazily.
2. Still missing (dynamic context regenerated — the Daily Mix case): **patch the clicked track in
   as current** (provider `Context`, metadata `context_patched:"true"`, cursor 0 of the resolved
   list becomes next) and log `queue.skip-miss`. Never play an unrelated index.

### 7.4 `EnqueueLocalAsync` / `PlayNextAsync` — q-uid minting rule (FIXTURE-C)
**The active device mints q-uids.** Concretely:
- We are active: `session.EnqueueUser`/`EnqueueNextUser` mint `q{n}` immediately (local adds AND
  inbound `add_to_queue`/`set_queue` rows arriving with `uid:""`), and our PutState publishes them
  so remote `next_track` jumps can target them.
- We are a viewer: outbound `add_to_queue` sends `uid:""` (unchanged today); outbound `set_queue`
  echoes existing rows' uids + metadata **verbatim** and sends new rows with `uid:""` — the active
  device mints. Never invent a q-uid for a queue we don't own.

## 8. Startup recovery — full session restore (F9)

New `SessionRecovery` step in the controller, run on the **first cluster fold after connect**
(and on reconnect after a dealer drop when we have no live local session):

```
if (cluster has player_state && no OTHER device actively playing):
    hydrate current track metadata (cache-first)
    snap = session.ReplaceFromCluster(delta, hydratedCurrent)
        - Current       = player_state.track   (provider-aware: FIXTURE-A current is provider "queue")
        - History       = prev_tracks (context rows keep uids)
        - UserQueue     = next_tracks where provider=="queue", IN WIRE ORDER (reordered order preserved)
        - Upcoming      = next_tracks context rows, then autoplay rows (AutoplayContextUri from their context_uri)
        - options       = shuffle/repeat from cluster; ClusterQueueRevision kept
    position = extrapolate(position_as_of_timestamp, timestamp, server_timestamp_ms)  // existing math
    publish PAUSED at position; audio host NOT started
on Resume: LoadAndPlayCurrentAsync (fast-start) at the stored position
background heal: context-resolve(context_uri) → match by uid → extend Upcoming beyond the
    cluster window + restore _nextPageUrl; on uid mismatch keep cluster rows (they are the truth
    of the live session) and log queue.recovery.heal-miss
if another device IS active: viewer mode exactly as today (no local session).
```

Track rows are hydrated from cluster metadata immediately (title/artist/images ride in the wire
metadata — FIXTURE-A rows are self-sufficient for display) and enriched async via the existing
`MaybeEnrichCurrent` machinery, so the queue panel is fully populated before first paint.

Local persistence (offline cold start, cluster unavailable): **deferred to a follow-up** —
recovery covers the online case; note it in §13 open questions.

## 9. Wire changes (`OutboundEnvelope.cs`)

```csharp
// NEW — FIXTURE-B parity. endpoint "next_track"; track carries uri+uid+full metadata map
// plus TOP-LEVEL album_uri/artist_uri duplicates (the capture has them outside metadata).
public static byte[] NextTrack(QueueEntry row, string deviceId, string commandId, string intentId);
```

- Plain transport-next keeps `skip_next` (works today); row-jump uses `next_track`.
- `add_to_queue` gains the real `q{n}` uid (§7.4).
- `set_queue` unchanged in shape; `next_tracks` now built from the session when we're active
  (user queue in display order with q-uids, then context continuation with uids +
  `metadata.original_index`/`view_index`, then the autoplay tail and `meta:page`/`delimiter`
  markers echoed through per FIXTURE-C) — verify against FIXTURE-A/C ordering in the golden test
  before switching `PlayNextAsync` over. As a viewer: existing rows verbatim, new rows `uid:""`
  (§7.4). Bodies can exceed 500 KB pre-gzip (FIXTURE-C): keep the gzip path, no row caps anywhere
  in the wire layer.
- Inbound parsers: `next_track` endpoint + track payload (§6); `set_queue` full reconcile (§6).

## 10. UI rework

### `QueuePanel.cs`
- Row click / cover-play → `b.Player.SkipToQueueItemAsync(entry.ItemId)`. **Delete**
  `BuildPlaybackOrder` + `PlayQueueEntry`'s ordered-page fallback entirely (F1). Single-track
  fallback (`PlayTrackAsync`) only when `ItemId.IsNone` (defensive).
- History rows become clickable (cursor-back §4.5) — drop the `CanRemove`-gated non-interactivity
  for history; keep the 0.62 opacity.
- The content-hash remount signature (`QueuePanel.cs:93-110`) is **deleted, not replaced** — do
  NOT re-key the list on the revision (that is a full remount on every mutation; it is the cause of
  the "whole list refreshes" jank and it defeats all row animation). The list source is a stable
  keyed sequence (§10.2); the reconciler diffs it in place. `QueueRevision` is used only to know a
  push happened, never as a list `Key`.
- Optimistic remove switches to `RemoveQueueItemAsync(ItemId)`; the local `display` mirror and
  `displayEpoch` machinery stays (server truth wins on next push).
- Buckets/sections render from `snap.UserQueue`/`snap.Upcoming` provider runs — the existing
  `AddNextUpSections` autoplay-split logic simplifies to a provider check (no more
  `IsAutoplayEntry` heuristics).
- Reorder: no drag UI this branch; context-menu "Move up/down" is NOT added either (decision:
  design-only).

#### 10.1 Bucket presentation — Apple Music parity (user decision 2026-07-08)

Reference: Apple Music's queue sheet. Every bucket renders as a distinct titled section **whenever
it is non-empty** — never folded together, never header-less:

| Order | Section | Header treatment | Rows |
|---|---|---|---|
| 0 | **History** (ABOVE the anchor — revealed by scrolling UP) | title + **`Clear` text button**; rows dimmed 0.62, clickable (cursor-back), per-row remove in the context menu | `snap.History` (oldest first, newest row adjacent to Now Playing) |
| 1 | Now Playing — **the initial scroll anchor** | existing header row (art + title + like) | `snap.Current` |
| 2 | **Next in queue** | title + row count + **`Clear` text button** (right-aligned) | `snap.UserQueue` |
| 3 | **Next up** | title + "Playing from {source}" subtitle, source **clickable** (`contextHref`, exists today) | `snap.Upcoming` where Provider==Context |
| 4 | **Autoplay** | `∞` glyph + title + "Similar music will keep playing" subtitle + the on/off toggle (replaces the current footer-toggle-only treatment) | `snap.Upcoming` where Provider==Autoplay |

History placement (Apple Music parity, user decision 2026-07-08): the panel **opens scrolled to
the Now Playing row** with History off-screen above; scrolling up reveals it. Implementation
notes:
- Scroll-to-anchor on mount (scroll to the Now Playing row index after first realize), and
  **prepend-stable anchoring**: when History gains a row (track advance) or rows are removed,
  adjust the scroll offset by the delta of content above the anchor so the viewport never jumps.
  Mind the engine gotchas: scroll targets are incremental deltas (AbsoluteRect already includes
  the live scroll transform), and OnRealized does not fire on a component root — use
  Context.HostNode.
- History `Clear` wires to a new session op `ClearHistory()`; per-row remove reuses
  `RemoveQueueItemAsync`. Both are local-session-only (history is our local model; no wire verb).

- **Clear** wires to a new session op `ClearUserQueue()` (drop all UserQueue rows, one revision
  bump) surfaced as `IPlayback.ClearQueueAsync()`. Active device: local op. Viewer: defer (Spotify
  has no clear verb; a set_queue rewrite lands only with §13.4's golden test) — hide the button in
  viewer mode.
- Autoplay OFF with autoplay rows absent: the Autoplay section still renders as the toggle
  affordance (current footer behavior, promoted to a real section header). Autoplay ON: rows list
  under it, exactly like the reference.
- `∞` glyph: check `Design/Glyphs.cs` for an existing infinity/repeat-forever glyph before adding
  a new codepoint.
- Empty buckets render nothing (no placeholder rows) — parity with the reference, and matches the
  existing `AddBucket` behavior.

#### 10.2 Reactivity & motion — the quality bar (user feedback 2026-07-08)

The current panel feels "mid": it refreshes the whole list on any change, has no motion, and shows
History unconditionally. Non-negotiable requirements for this rework:

1. **In-place reconciliation, never a list remount.** The list renders from a stable keyed sequence
   where each row's key is its `QueueItemId` (stable across reorder/remove/continuation-append by
   construction — that is why §4 mints them). Header/section rows get stable string keys
   (`"hdr:nextup"` etc.). On a snapshot push the reconciler diffs old vs new by key and patches only
   what changed — one row's now-playing state, an inserted queue row, a removed row — with zero
   churn to untouched rows. The engine's keyed reconciler + `ItemsView.CreateBound` signals-first
   row (row built once, re-renders in place on `Index`) already do this; the job is to STOP fighting
   it (delete the remount signature) and feed it a keyed list.
2. **Apple-Music-class motion**, via the engine's declarative animation surface (see CLAUDE.md /
   `docs/plans/animation-engine-rework-design.md` — `AnimEngine` + `Element.{Transition, Enter,
   Exit, Layout, Stagger}` + `MotionTok`, analytical spring, reduced-motion-as-a-value):
   - **Insert** (add-to-queue, continuation append): `Element.Enter` — fade + slide/height-expand
     in. Row does not pop.
   - **Remove** (swipe/clear/consume): `Element.Exit` — fade + height-collapse; siblings close the
     gap via layout animation, not a jump.
   - **Reorder & positional shift** (a row above is removed, now-playing advances, future drag):
     `Element.Layout` FLIP (`relativeTarget`) so rows glide to their new Y.
   - **Now-playing / state changes**: cross-fade the play/pause/equalizer affordance; no full-row
     rebuild.
   - Respect reduced-motion as a value (never a hook branch), per the engine contract.
3. **History hidden until scrolled to** (§10.1 bucket 0): it lives above the anchor and is
   off-screen on open — it must NOT be visible in the resting viewport. Verify on-device that the
   panel opens anchored at Now Playing with History requiring an upward scroll.
4. **No per-frame allocation / no dropped frames** on a large queue (the FIXTURE-C 375-row case):
   virtualization stays on (the panel already uses a bound/virtualized `ItemsView`); the keyed diff
   must not realize off-screen rows. This is a scroll-perf gate, checked with `FG_SCROLL_TRACE` if
   it regresses.

This is a **visual-iteration** deliverable: build, run the app, screenshot the queue panel, compare
against the Apple Music reference, tune spring/stagger tokens, repeat — not a one-shot. It is driven
after the correctness rework lands so motion is tuned on correct data.

### `DetailTracks.cs`
- No signature changes; §7.3 fixes land in the controller/resolver. `IsNaturalContextOrder` stays
  as the *routing* predicate (natural → skip_to; sorted → embedded pages) — correct once the
  index fallback is gone.

### `PlayerBar.cs`
- **Delete** the unmounted `QueueButton` flyout component (F12). The rail toggle is the queue
  surface.

## 11. Deletions (explicit)

- `QueueCore` positional id minting/parsing (`Playback.cs:83-107,231-252`) — replaced by
  `PlaybackSession` ids.
- `NowPlayingProjection.MapQueue`'s `n{i}` scheme (`PlaybackProjection.cs:425-450`) — unified
  mapper.
- `NowPlayingProjection.SetLocalContext`/`SetLocalQueue` split setters → `ApplyLocalSnapshot`.
- `QueuePanel.BuildPlaybackOrder`, `CanSkipWithinContext`/`ContextFallbackIndex` remnants (already
  reverted), and the content-hash remount signature — deleted with NO revision-key replacement
  (§10.2.1); the list reconciles in place off `QueueItemId` keys.
- `PlayerBar.QueueButton` (dead).
- `BuildEvent`'s snapshot-row provider scan + dead ternary (`PlaybackController.cs:807-812`).

## 12. Test plan (Wavee.Tests)

New `QueueSessionTests.cs` (pure, no I/O) — every §4 semantic:
skip-to-upcoming keeps user queue; skip-to-queue-row drains predecessors; history cursor-back;
next-order (queue → context → autoplay → delimiter-stop); id stability across
reorder/remove/append; q-uid minting; shuffle anchor; `keepUserQueue` matrix; revision
monotonicity.

`QueueRecoveryTests.cs` — feed **FIXTURE-A** through `ReplaceFromCluster`:
- Current = `spotify:track:3ySSbGT5BepfePnva86js7`, provider Queue, uid `q0`.
- UserQueue order exactly `q3,q1,q4,q5,q2` (reorder preserved).
- Upcoming: 2 context rows (uids `fef9f10e…`, `e1745149…`), jam-patch row surfaced as context,
  then autoplay rows with `AutoplayContextUri = spotify:station:album:0apIvboeRy3QYd13K5Dfj4`;
  `meta:page`/`delimiter` not surfaced but delimiter-stop honored.
- History = the 2 prev context rows. Revision echo = `12613583692104578720`.

`OutboundEnvelopeTests` — golden-compare `NextTrack(...)` against FIXTURE-B req1/req2 (field-set
equality, not byte equality; metadata passthrough), and `SetQueue(...)` against FIXTURE-C
(existing-uid echo, new-row `uid:""`, autoplay tail + marker rows preserved).

`ConnectControllerTests` additions for FIXTURE-C: inbound `add_to_queue` with `uid:""` while we're
active mints the next `q{n}`; inbound 375-row `set_queue` reconciles without truncation and the
minted uids surface in the next PutState.

`ConnectControllerTests` — inbound `next_track` with payload jumps by uid; `skip_next` bare still
advances; set_queue full reconcile; the DEBUG current==NowPlaying-row assert never fires across
the existing scenario suite.

Existing suites (`ConnectProjectionTests`, `ConnectControllerTests`, `WireMapperTests`) updated for
the new `QueueEntry` shape. Green bar = full `dotnet test` + on-device checklist: queue click
(local + targeting web player), official-app queue click against Wavee, restart-and-resume with a
FIXTURE-A-shaped session, Daily Mix natural-order click (the IU repro), 500-track playlist click
past page 8.

## 13. Open questions (defaults stated — flag before implementation if wrong)

1. **Skip-miss patch (§7.3.2)**: on identity miss in a regenerated dynamic context, default =
   patch the clicked track in as current over the fresh resolve. Alternative: play the fresh
   resolve from the top.
2. **Skipped user-queue rows drop on forward jump (§4.4)** — inferred drain semantics; no capture
   proves it. If Spotify actually keeps them, flip to keep (one-line change, test pinned either way).
3. **Offline cold-start persistence (§8)** — deferred; follow-up branch.
4. **`set_queue` outbound rebuild (§9)** — switching `PlayNextAsync` from echo-cluster-verbatim to
   session-built arrays happens only after the golden test matches FIXTURE-A ordering.
