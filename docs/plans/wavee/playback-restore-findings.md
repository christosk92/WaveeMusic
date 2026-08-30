# Playback restore — edge-case findings

Status: **FIXED** — see “As fixed” at the end. Diagnosis + fix design: 2026-08-14; implemented the same day.
Grounding: `PlaybackController`, `PlaybackSession`, `NowPlayingProjection`, `ClusterMapper`, `TransferState` / `ProtoTransferStateDecoder`, `PlaybackBridge`.
Prior locked intent: `docs/plans/wavee/queue-rework-proposal.md` §8 (F9) — full session restore from cluster, **shown paused**, resumable instantly. Local persistence was explicitly deferred (§13.3).

PlayPlay is an opaque key-deriver behind `ITrackResolver.ResolveAsync`. Restore paths that call it are noted as a boundary only.

---

## Restore architecture

Two live restore paths exist. Neither is a launch-time `SessionRecovery`. Nothing local persists current track / position / queue / shuffle / repeat (settings persist volume, device, EQ, crossfade, quality, autoplay-enabled — `AppSettings.cs` `playback.*` keys).

**Cluster ingest.** `ClusterIngest` maps `Cluster` → `ClusterDelta` (`ClusterMapper.Map` 15–64: active device, current `ProvidedTrack`, `context_uri`, playing/paused/buffering, `position_as_of_timestamp` + `timestamp` + `server_timestamp_ms`, duration, shuffle/repeat, `next_tracks`, `prev_tracks`, restrictions, speed, `queue_revision`). `NowPlayingProjection.OnCluster` 365–444 folds that slab. Viewer (`ActiveDeviceId != us`): cluster owns track, options, restrictions, and `MapQueue(next_tracks)` (prev_tracks discarded, 617–618). Active-with-local-session: local snapshot owns track/queue/options. **Active-without-local-session** (cold start, cluster still names us): track/position/context URI are taken from the cluster, but queue is **not** (`if (!weActive)` at 429–433), and shuffle/repeat stay at local defaults (409).

**Ghost resume (play while `_session.Current is null`).** `ResumeAsync` / inbound `resume` / transfer-to-self → `LocalResumeAsync` 1558–1566 → `GhostResumeAsync` 1607–1638. Consumes `_projection.CurrentTrack`, `ContextUri`, `PositionMs`, `LastCluster`. `SeedSessionFromCluster` 1645–1657 calls `PlaybackSession.ReplaceFromCluster` (or a one-track `SetContext` if no cluster). Then **always** `SwitchHost(_audioHost)`, `ResolveAsync`, `Load`, optional episode overwrite, `Seek`, **`Play()`**, `Emit(Started)`. Does **not** go through `LoadAndPlayCurrentAsync` (no fast-start, no continuation prefetch, no video, no paused).

`ReplaceFromCluster` 514–556 files `next_tracks` by `provider` (`queue` → UserQueue in wire order; else Upcoming, autoplay provider sets `AutoplayContextUri`). Copies shuffle/repeat. **Clears History.** Does not read `c.PrevTracks`. Cursor = `-1` (current stands outside the context spine). Comment at `SeedSessionFromCluster` 1642 (`prev_tracks restore History`) is stale; the session comment 512–513 and `QueueRecoveryTests` 98 are the truth.

**Inbound transfer.** `HandleInboundPlayOrTransferAsync` 1046–1055 sets `_connectOriginatedPlayback = true` then `HandleInboundTransferAsync` 1092–1207. Decode failure → `LocalResumeAsync` (ghost). `ProtoTransferStateDecoder` 12–48 yields `TransferWireState`: context URI/url/metadata, current uid/track, `Queue` + `IsPlayingQueue`, timestamp/position/speed/paused, shuffle/repeat. Modes from `command.options`: `restore_paused`, `restore_position`, `restore_track`, `retain_session` (1215–1236). Context is **re-resolved** (`_contexts.ResolveAsync`); current is uid→uri (`FindStartIndex` 99–106, **no index**); miss hydrates the URI; empty URI + `always_play_something` takes `tracks[0]`; else ghost fallback. User queue from `state.Queue` (skip head if `IsPlayingQueue`). `SetTransferredContext` 195–237 (clears History + AutoplayContextUri + optionally UserQueue) then `EnqueueUser`, `SetShuffle`/`SetRepeat`. `LoadAndPlayCurrentAsync(paused ? Paused : Started, position, paused)` 1189.

**Empty cluster / idle.** `GhostResumeAsync` 1611: `if (track is null) return`. No local snapshot. Player stays empty.

**Previous after any restore.** `LocalPrevAsync` 1587–1602: `PositionMs > 3000` → seek 0; else `PlaybackSession.Prev` 388–394 (history tail, else prior context row). Empty history + cursor `-1` → `Prev()` returns null → **no-op**. `ApplyLocalSnapshot` on `Started`/`Resumed`/`TrackChanged` forces `_canSkipPrev = true` (282–286). UI Previous is enabled.

**Video-on-launch.** Not a playback-session rule — a placement rule. `WaveeSettings.VideoPreferredPlacement` stores **where**, never **whether** (`AppSettings.cs` 124–127; `PlaybackBridge` 177–188; `PlacementState.Initial` Requested/Live = None). Ghost forces the audio host (1616–1617). Inbound transfer/play is audio-first via `_connectOriginatedPlayback` (`KindFor` 265–266).

---

## Cluster fields: consumed vs dropped (GhostResume)

| Cluster field | GhostResume |
|---|---|
| `player_state.track` (uri/uid/provider/metadata) | Consumed as current (`ReplaceFromCluster` 543–548) |
| `context_uri` | Consumed |
| `next_tracks` provider `queue` | Consumed → UserQueue (wire order) |
| `next_tracks` context/autoplay + markers | Consumed → Upcoming; markers not surfaced |
| `prev_tracks` | **Dropped** (`_history.Clear()` 519; `c.PrevTracks` unread) |
| `options` shuffle/repeat | Consumed as flags (no reshuffle — cluster order kept) |
| `is_paused` / `is_playing` | **Dropped** — always `Play()` 1634 |
| `position_as_of_timestamp` + timestamps | Consumed via already-aged `_projection.PositionMs` 1623 |
| `playback_speed` | Dropped on the host (seek + play at 1.0) |
| `queue_revision` | Copied onto the snapshot |
| restrictions (`disallow_skipping_prev`) | Dropped — local `Started` forces `CanSkipPrev=true` |
| `next_page_url` (not on cluster) | `_nextPageUrl = null` 1651 — playlist/station paging **lost** |
| episode Herodotus | Overwrites cluster position when `micros > 0` 1624–1630 |

Transfer additionally **drops** the cluster window: it re-resolves the context URI (server-now ordering) and only restores `TransferWireState.Queue` as UserQueue. No `prev_tracks` exist on the transfer proto. Autoplay tail is not in `TransferWireState` (`SetTransferredContext` nulls `_autoplayContextUri` 204). `_nextPageUrl` **is** taken from the resolve (1185) — the one paging advantage over ghost.

---

## Edge-case matrix

Legend: **OK** = restored correctly for that path. **BROKEN** = path runs but the dimension is wrong. **MISSING** = no code. Transfer columns share `HandleInboundTransferAsync`; a mode only changes the cells it gates.

| Dimension | GhostResume | T:RestorePaused | T:RestorePosition | T:RestoreTrack | T:RetainSession | Cold start, empty cluster |
|---|---|---|---|---|---|---|
| Current track + position | **OK** track; position from `PositionMs` (1623–1633). Always then plays. | **OK** `LoadAndPlayCurrentAsync` at `PositionMs` (1162–1189). | **OK** when `extrapolate` and !paused: `position += (Now()-TimestampMs)*Speed` (1164–1168). Clock is client UTC, not the projection's server-age. | **OK** on uid/uri hit (`FindStartIndex` 1136). **BROKEN** on miss — see fallback row. | **OK** — mode does not touch track/position. | **MISSING** — 1611 returns; no local snapshot. |
| Paused | **BROKEN** — `Play()` 1634; `is_paused` unread. Violates §8 “publish PAUSED”. | **OK** iff option `== "restore"`: `paused = … && state.Paused` (1163); `initiallyPaused` skips `Play` (1794/1810). Tested (`ConnectControllerTests` 598–638). Any other string → plays. | **OK** — extrapolation gated on `!paused` (1164). | **OK** — does not force play/pause. | **OK** — unused. | **MISSING** — nothing to pause. |
| History / Previous | **BROKEN** — History cleared (519); `prev_tracks` ignored. After restore: `CanSkipPrev=true` (286) but `<3s` + empty history + cursor `-1` → `Prev()` null (392–393) → **enabled Previous no-ops**; `>3s` restarts (1594). | **BROKEN** — `SetTransferredContext` `_history.Clear()` (203). Same Previous trap. | same | same | **MISSING** as session-retain — always clears History. Only `_remoteSessionId` (1175–1177). | **MISSING** |
| UserQueue | **OK** — `provider=="queue"` rows, wire order (524–532). **Fragile:** no `metadata.is_queued` fallback (unlike `ParseWireEntries` 2761–2762). Empty provider → flattened into Upcoming. | **OK** — `state.Queue` hydrated, `EnqueueUser` (1149–1180). Head skipped if `IsPlayingQueue`. Transfer queue is the proto user-queue, not mixed `next_tracks`. | same | same | same (queue always rebuilt, not retained) | **MISSING** |
| Upcoming / context | **BROKEN** — cluster `next_tracks` window only; `_nextPageUrl = null` (1651); no `MaybeStartContinuationFetch` (Ghost bypasses `LoadAndPlayCurrentAsync`). Rest of a long playlist never pages in. Cursor `-1` is correct for “current stands alone”. | **OK-ish** — full resolve + `NextPageUrl` (1185). **BROKEN** vs live session: server-now context can diverge from the cluster window (playlist edit / Daily Mix regen). | same | **BROKEN** on identity miss: current patched outside context (`cursor=-1` 230); `Next()` then plays `context[0]`, not the successor of the transferred track. | same rebuild | **MISSING** |
| Autoplay tail | **OK** flag if cluster already carries `provider:"autoplay"` (`AutoplayContextUri` 535). **BROKEN** when the window is exhausted: `_contextIsInfinite` from URI (1652); `CanAutoplay` returns false for station/radio (1980); `_nextPageUrl` null; no prefetch. Track-end → `Ended`. | **BROKEN** — `_autoplayContextUri = null` (204); `_autoplayLatchedFor = null` (1187). Station URI sets `_contextIsInfinite` so autoplay fetch is suppressed. Finite context will prefetch autoplay via `LoadAndPlayCurrentAsync` → `MaybeStartContinuationFetch` (1857) — better than ghost, but the *transferred* autoplay tail is gone. | same | same | same | **MISSING** |
| Shuffle | **OK** — flag copied, order is the cluster’s already-shuffled `next_tracks` (552). | **OK** — `SetShuffle(state.Shuffle)` after context seed (1181–1182). Leftover `_shuffle` from a prior local session can reshuffle inside `SetTransferredContext` (226) before `SetShuffle`; if both true, `SetShuffle` is a no-op and the leftover shuffle already ran. | same | same | same | **MISSING** (stays Off). Extra: if cluster still names **us** active, `OnCluster` 409 skips applying cluster shuffle. |
| Repeat | **OK** — `c.Repeat` (553). | **OK** — `SetRepeat(state.Repeat)` (1184). | same | same | same | **MISSING** (same we-are-active fold skip). |
| Episode position | **BROKEN** — Herodotus **overwrites** cluster position whenever `micros > 0` (1624–1630), even if the cluster is newer. | **OK** if `PositionMs > 0` — seek wins, `MaybeSeekEpisodeResumeAsync` skipped (1795–1796). **BROKEN** if transfer position is 0: Herodotus runs as fallback (could be right). | **OK** — extrapolation then same seek/Herodotus gate. | n/a | n/a | **MISSING** |
| Unplayable / mismatch fallback | **BROKEN** — `ResolveAsync` throw → `ReportPlaybackError` (1621), no skip-to-next. No uid/index/context-head reconcile (cluster current is used as-is). | Decode/empty-current → ghost (1096–1144). Identity miss → `HydrateOneAsync` synthetic (1138, 1546–1555) then resolve fail → toast, **no auto-skip**. `FindStartIndex` never falls back to index (F2, 105). `always_play_something` only when URI **and** GID are empty (1139). | n/a | **BROKEN** — see left; `only_current` (outbound default, `OutboundEnvelope` 309) never plays context-head. | n/a | **MISSING** |
| Queue UI / `QueueRevision` | **OK after Play** — `Emit(Started)` → `ApplyLocalSnapshot` → `WindowQueue` (314–327) → `PlaybackBridge.PushState` copies `Queue` and `BumpQueueRevision` (986–987, 1028–1045). **BROKEN before Play** when cluster lists us active: `OnCluster` keeps empty local `_queue` (429–433); now-playing can show the track with an empty panel. Viewer (other device / nobody) gets `MapQueue` (no History). | **OK** — `LoadAndPlayCurrentAsync` publishes the transferred snapshot; revision bumps on content fold. History still empty so the panel’s forward-looking sections are correct; Previous is the lie. | same | same | same | **MISSING** — Queue stays `[]`, `QueueRevision` stays 0 until a real content fold. |

### Asymmetry (ghost vs transfer)

| Capability | Ghost | Transfer |
|---|---|---|
| UserQueue from live window | Yes (`next_tracks` / provider) | Yes (proto `Queue` only) |
| Autoplay tail from live window | Yes if present on cluster | No (cleared) |
| Full context resolve + `next_page_url` | No (`_nextPageUrl=null`, no prefetch) | Yes |
| History from `prev_tracks` | No | No (not on proto) |
| Honor paused | No | Yes if `restore_paused=="restore"` |
| Fast-start / continuation prefetch | No (bypasses `LoadAndPlayCurrentAsync`) | Yes |
| Episode: cluster/transfer pos vs Herodotus | Herodotus wins | Transfer pos wins if `>0` |
| Video | Forced audio host | Audio-first (`_connectOriginatedPlayback`) |
| Fallback on empty current | No-op | Ghost, then possibly `always_play_something` |

---

## Fix design

### 1. Launch `SessionRecovery` (implements queue-rework §8; currently absent)

On the **first cluster fold** after announce (and on reconnect when `_session.Current is null`):

- If another device is active → viewer, unchanged.
- If cluster `HasTrack` and (nobody active **or** we are the stale active device):
  1. `snap = _session.ReplaceFromCluster(delta, hydratedCurrent)` with the History fix in (2).
  2. Publish **Paused** at extrapolated position via `ApplyLocalSnapshot(snap, EvKind.Paused)`. Do **not** `ResolveAsync` / `Play`.
  3. Background heal: `ResolveAsync(context_uri)` → match current by **uid, then URI, then index, then context-head** (rule below); extend Upcoming; restore `_nextPageUrl`; on uid mismatch keep cluster rows (they are the live session) and log `queue.recovery.heal-miss`.
- `LocalResumeAsync`: if session already seeded, `LoadAndPlayCurrentAsync(Started, storedPosition)` (fast-start). Ghost becomes this path; delete the audio-only duplicate in `GhostResumeAsync`.

### 2. History from `prev_tracks`

`ReplaceFromCluster`: after filing `next_tracks`, push `c.PrevTracks` (oldest→newest) into `_history` with uid/provider preserved, cap `HistoryCap`. Stop clearing-without-refill. Update `SeedSessionFromCluster` comment (1642) to match. Viewer `MapQueue` should surface a History tail the same way (`_ = prev` at 621 is the viewer half of the same bug).

`CanSkipPrev` while locally active: `history.Count > 0 || cursor > 0 || position > 3000` — never the blanket `true` on `Started` (286). `LocalPrevAsync`: if `Prev()` is null and position `> 0`, seek 0 (restart); if already at 0, no-op **and** disable the button.

### 3. `is_queued` + autoplay classification

`ReplaceFromCluster`: treat a row as UserQueue when `provider=="queue"` **or** `metadata.is_queued=="true"` (mirror `ParseWireEntries` 2761–2762). Keep autoplay via provider **or** `autoplay.is_autoplay=="true"` (already used in `ApplySetQueue` 582).

### 4. Unify restore onto `LoadAndPlayCurrentAsync`

Ghost/recovery must not bypass it. That restores fast-start, `MaybeStartContinuationFetch`, prepared-next, and the paused flag. Keep `_connectOriginatedPlayback` audio-first for inbound transfer; launch recovery is local, not Connect-originated.

### 5. Episode position

Single rule on both paths: **cluster/transfer position wins when `> 0`**. Call `EpisodeResumeMicros` only when the restored position is 0. Ghost 1624–1630 currently inverts this.

### 6. Unplayable / identity miss

Shared resolver used by transfer, heal, and local snapshot:

1. uid match in resolved rows
2. URI match
3. `skip_to` / saved index if in range **and** the row’s URI is still playable (never a blind index across a regenerated mix — F2)
4. context-head last resort only when the caller opted in (`always_play_something` or launch recovery with “play something paused”)
5. hydrate-and-patch current outside the spine (today’s transfer miss) **must** set cursor so `Next()` advances to the first resolved row that is *after* the patched URI in the saved order, not `context[0]`
6. `ResolveAsync` / host error → skip to `PreviewNext()` once, then surface `ReportPlaybackError` if the whole window is dead

### 7. Autoplay / station after restore

Keep cluster autoplay rows. After heal: if context URI is infinite (`ContextResolve.IsInfinite`), restore `_nextPageUrl` / station continuation rather than blocking `CanAutoplay` and dying at the window edge. `_autoplayLatchedFor` stays null until a *new* autoplay fetch (do not latch just because we restored an existing tail).

### 8. Local session snapshot (empty-cluster fallback)

New persisted blob, `TransferWireState`-shaped, **not** a second queue model.

```
PlaybackSessionSnapshot v1
  contextUri, contextKind          // playlist/album/station/collection/…
  currentUri, currentUid, currentIndex
  positionMs, paused               // always write paused=true on suspend/crash-safe flush
  shuffle, repeat
  userQueue: [{uri, uid}]          // provider queue only
  autoplay: bool + autoplayContextUri?
  writtenAtUnixMs
```

**Write** (debounced): track boundary, queue mutation (`Enqueue*` / `ApplySetQueue` / skip), pause, app suspend / `BecameInactive`. Never write because position ticked.

**Read:** `LocalResumeAsync` / `SessionRecovery`:

1. cluster `HasTrack` → cluster (paused)
2. else snapshot if `currentUri` non-empty → restore **Paused**, never autoplay-on-launch
3. else idle

Reconciliation on consume: uid → URI → index → context-head (step 6). Video: snapshot is audio-session only; placement persistence stays “preferred, not live” (already correct).

**Do not** persist History (rebuild empty; Previous restarts). Do not persist the full Upcoming spine (heal from context URI). UserQueue URIs are the one local-only set the cluster will not have when empty.

### 9. Stale “we are active” fold

`OnCluster`: `weActive && !_hasLocalContext` must either run `SessionRecovery` or fall through to `MapQueue` + cluster shuffle/repeat. Today’s gate (409, 429) is why launch can show a track with an empty queue.

---

## Tests to write (`Wavee.Tests`, pure logic, no GPU)

Reuse `FakeContextResolver`, `ProtoTransferStateDecoder`, `QueueRecoveryTests` fixture-A, `ConnectControllerTests.Make`.

1. **`ReplaceFromCluster_ImportsPrevTracksIntoHistory`** — fixture-A `prev_tracks` become `snap.History` (oldest first, uids kept); `QueueRecoveryTests` line 98 inverted.
2. **`ReplaceFromCluster_IsQueuedMetadataWithoutProvider_GoesToUserQueue`** — `provider:""` + `is_queued:"true"` → UserQueue, not Upcoming.
3. **`SessionRecovery_FirstCluster_PublishesPaused_DoesNotPlay`** — cluster with track + paused/playing, nobody else active → snapshot seeded, host has no `play`, `IsPlaying==false`, position extrapolated; Resume then `play` + seek.
4. **`SessionRecovery_OtherDeviceActive_DoesNotSeedLocalSession`** — viewer `MapQueue` only; `_session.Current` stays null.
5. **`OnCluster_WeAreStaleActive_FillsQueueFromCluster`** — `active_device_id==us`, no local session → Queue non-empty (UserQueue+Upcoming), shuffle/repeat from cluster.
6. **`GhostResume_EmptyCluster_NoOps`** — no load/play; with snapshot present (once (8) lands) seeds paused from snapshot instead.
7. **`LocalPrev_AfterRestore_EmptyHistory_Under3s_NoopsAndCanSkipPrevFalse`** — after `ReplaceFromCluster` without prev; `CanSkipPrev` derived; `PreviousAsync` does not `play` a different uri.
8. **`LocalPrev_AfterRestore_Over3s_RestartsCurrent`** — existing >3s rule still holds.
9. **`GhostResume_DoesNotOverwriteClusterEpisodePosition_WithStaleHerodotus`** — cluster pos 10_000, Herodotus 5_000 → seek 10_000. Herodotus used only when cluster pos is 0.
10. **`Transfer_RestorePaused_Restore_DoesNotPlay`** — already exists (598); keep. Add **`Transfer_RestorePaused_MissingOption_DoesNotPlayIfStatePaused`** once the option default is “restore”.
11. **`Transfer_RestorePosition_Extrapolate_Paused_DoesNotAge`** — paused + extrapolate → seek == `PositionAsOfTimestamp`.
12. **`Transfer_RestoreTrack_UidHit_IgnoresDivergentIndex`** — resolved list shuffled vs saved index; uid wins.
13. **`Transfer_RestoreTrack_UriMiss_IndexFallback_ThenContextHead`** — playlist edited; uid miss, URI miss, index in range + playable → that row; index out of range → head. Assert `Next()` is the successor, not an unrelated `tracks[0]` while a patched current is playing.
14. **`Transfer_RestoreTrack_OnlyCurrent_EmptyUriEmptyGid_FallsBackToGhost`** — not `always_play_something`.
15. **`Transfer_AlwaysPlaySomething_NoCurrent_PlaysResolvedHeadPausedOrPerOptions`**.
16. **`Transfer_RetainSession_DoNotRetain_MintsNewRemoteSessionId`** / **`Retain_KeepsExisting`** — playback-id only; session buckets still rebuilt (document that so it cannot regress into “retain means keep queue”).
17. **`Transfer_UserQueue_IsPlayingQueue_SkipsHead`** — current is queue[0]; remaining enqueue.
18. **`Transfer_DropsAutoplayTail_FiniteContextPrefetches`** — after transfer, `AutoplayContextUri` null; `MaybeStartContinuationFetch` eligible when remaining ≤5 and not infinite.
19. **`GhostResume_StationContext_DoesNotEndWhenWindowExhausted`** — after (7): infinite context keeps paging; `Ended` not emitted at last cluster row.
20. **`SeedFromCluster_ClearsNextPageUrl_HealRestoresIt`** — heal from resolve fills `_nextPageUrl`; without heal, `RemainingInContext` equals cluster window size.
21. **`UnplayableCurrent_AfterRestore_SkipsToPreviewNext`** — resolver throws on current, next row loads.
22. **`LocalSnapshot_RoundTrip_PausedNeverAutoplays`** — write snapshot, empty cluster, recovery → same uri/uid/index/pos/shuffle/repeat/user-queue URIs, `IsPlaying==false`, no `play`.
23. **`LocalSnapshot_Reconciliation_UidThenUriThenIndexThenHead`** — four cases, one theory.
24. **`PlaybackBridge_QueueRevision_BumpsOnGhostSeed`** — empty → fixture-A windowed queue increments revision; pause/seek does not.
25. **`VideoPreferredPlacement_DoesNotResumeLiveOnSeed`** — `SeedVideoSurfaceFromSettings`: Requested/Live stay None (existing placement tests; pin against restore).
26. **`ConnectOriginatedTransfer_KindForIsAudio`** — transfer of a has-video track does not call `LoadCurrentVideoAsync` (audio-first).

---

## Top-3 root causes

1. **No launch `SessionRecovery`, and GhostResume plays.** §8 required a paused seed from the first cluster. The only seed is a play-button ghost that `Play()`s, bypasses `LoadAndPlayCurrentAsync`, drops paused, drops paging, and overwrites episode position. Empty cluster has no local snapshot fallback (`queue-rework` §13.3 still deferred — and still the launch hole).

2. **History is defined as local-only and then never rebuilt.** `ReplaceFromCluster` / `SetTransferredContext` / viewer `MapQueue` all discard `prev_tracks`. Combined with `CanSkipPrev=true` on `Started`, Previous after restore is an enabled no-op (`<3s`) or a restart (`>3s`).

3. **Active-without-session cluster fold hides the queue.** When the announce echo still names us `active_device_id`, `OnCluster` takes the track but refuses cluster queue and options (`weActive` branches at 409 and 429). Launch can show now-playing with an empty queue until the user hits Play — at which point ghost autoplays and still has no History.

---

## As fixed

Date: 2026-08-14. The fix design above is implemented; this section is the closing matrix. Product code: `PlaybackController` (launch `SessionRecovery`, `TryRestoreFromSnapshotAsync`, `HealQueueFromContextAsync`, the unified ghost path), `PlaybackSession` (`prev_tracks` → History, `is_queued`/autoplay acceptance, `missCursor`), `NowPlayingProjection` (derived `CanSkipPrev`, the `weActive && !hasLocalContext` fall-through), `ContextResolve.ResolveRestoreIndex` (the shared ladder), `PlaybackBridge.MaybeWritePlaybackSnapshot` + `SessionSnapshotStore.UpdatePlayback` (the writer), `Program.cs` (process-exit flush). Agent-facing contract: `.claude/skills/wavee/session-restore.md`.

| Dimension | As fixed | Covering test |
|---|---|---|
| Current track + position | One pipeline: every restore path ends in `LoadAndPlayCurrentAsync` (fast-start, continuation prefetch, prepared-next). Cold start with an empty cluster restores from `session.json`. | `SessionRecovery_ThenResume_LoadsAtTheStoredPosition_WithoutASecondSeed`, `GhostResume_EmptyCluster_RestoresTheLocalSnapshotPaused_AndNeverAutoplays` |
| Paused | **A launch never starts music.** Recovery publishes `Paused` at the extrapolated position; the snapshot path loads `initiallyPaused: true`; the snapshot is always *written* `paused: true`. `restore_paused` now defaults to honouring the transferred paused state (only an explicit `kill` forces play). | `SessionRecovery_FirstCluster_SeedsThePausedSessionAndNeverPlays`, `InboundTransfer_DecodesInnerState_DerivesGid_AndStartsPausedAtPosition` |
| History / Previous | `prev_tracks` rebuild History (oldest→newest, uids kept, capped). `CanSkipPrev` is **derived** (`history > 0 ∨ cursor > 0 ∨ pos > 3 s`), never a blanket `true`. Previous steps back; with nothing behind it, past 3 s restarts and under 3 s is a true no-op with the affordance disabled. | `ReplaceFromCluster_ImportsPrevTracksIntoHistory`, `LocalPrev_AfterARestoreWithHistory_StepsBackIntoThePrevTracksTail`, `LocalPrev_AfterARestoreWithNoHistory_RestartsPast3s_AndNoOpsUnderIt` |
| UserQueue | Accepted via `provider:"queue"` **or** `metadata.is_queued:"true"` (the `ParseWireEntries` rule, now shared). Queue uris persist locally, so a custom queue survives a restart. | `ReplaceFromCluster_IsQueuedMetadataWithoutProvider_GoesToUserQueue`, `ReplaceFromCluster_FixtureA_MatchesSemantics` |
| Upcoming / context | The background heal re-resolves the context, extends Upcoming and restores `_nextPageUrl` — long playlists page in again. On an identity miss the cluster rows stay (they *are* the live session). | `SessionRecovery_StaleActiveEcho_StillFillsTheQueueAndOptions` |
| Autoplay tail | Accepted via provider **or** `autoplay.is_autoplay`; `_autoplayLatchedFor` stays null across a restore (a restored tail is not a new fetch), and an infinite context takes the continuation url so a station keeps paging instead of dying at the window edge. | `ReplaceFromCluster_FixtureA_MatchesSemantics` (tail + `AutoplayContextUri`) |
| Shuffle / Repeat | The stale-active fold no longer refuses cluster options — `weActive && !hasLocalContext` falls through to the cluster's queue *and* shuffle/repeat. | `SessionRecovery_StaleActiveEcho_StillFillsTheQueueAndOptions`, `OnCluster_WeAreStaleActive_FillsQueueFromCluster` |
| Episode position | One rule both paths: cluster/transfer position wins when `> 0`; `EpisodeResumeMicros` only at 0. | *(rule inlined in `LoadAndPlayCurrentAsync`; no dedicated test — see debt)* |
| Unplayable / mismatch | `ContextResolve.ResolveRestoreIndex` is the one ladder: uid → uri → saved index (in range **and** playable) → context head (opt-in; for a transfer only when the sender named no current). A full miss patches the current outside the spine with `missCursor` so `Next()` is the successor, not `context[0]`. Resolve failure skips to `PreviewNext()` once, then reports. | the six `ResolveRestoreIndex_*` tests, `InboundTransfer_DecodesInnerState_DerivesGid_AndStartsPausedAtPosition` |
| Queue UI / `QueueRevision` | A seed produces new rows and bumps the revision; a pause/seek/volume republish re-windows the same rows and coalesces (`QueueContentFold`). No more now-playing over an empty panel. | `QueueContentFold_IsStableAcrossARepublishOfTheSameEntries`, `QueueContentFold_ChangesWhenTheQueueContentChanges`, `QueueContentFold_IsOrderSensitive` |

Two assertions that **pinned** the old bugs were inverted: `QueueRecoveryTests` (History is now imported) and `ConnectProjectionTests` (`prev_tracks` now surface). `ConnectPublisherTests` gained `QueueSnapshot_CapsWireTracks_AndPublishesHistoryAsPrevTracks`.

### One deviation from the fix design

§6 numbered the context-head rung *above* "hydrate-and-patch the current outside the spine". Implemented the other way round for transfers: an explicitly transferred current (commonly a gid with an empty uri, and frequently absent from the resolved page — a phone playing a track the page does not list) **beats** the head. `always_play_something` means "play something rather than nothing", never "prefer the head over the track the sender says is playing" — which is also what the design's own tests 14/15 describe (head only when there is *no* current). Pinned by `InboundTransfer_DecodesInnerState_DerivesGid_AndStartsPausedAtPosition`.

### Test debt (not written)

Of the 26 planned tests, the restore-critical ones above landed (17 tests across four files). Still unwritten, in rough value order: the episode Herodotus-vs-cluster precedence case (9), the transfer-mode matrix beyond paused/track — `restore_position` extrapolation ageing (11), `retain_session` (16), `IsPlayingQueue` head-skip (17), autoplay-tail prefetch eligibility after transfer (18), station-window exhaustion (19), `next_page_url` heal (20), unplayable-current skip (21), and the video-placement pin (25). None of them gate the fixed behaviour; they widen coverage. **Final acceptance remains a user-run pass** against the matrix above — the paused cold start, Previous after a restart, a custom queue surviving a restart, and a station continuing past the window edge.
