# Handoff detail — Spotify Connect ↔ local playback (2026-09-11)

Companion to `handoff-20260911-connect-gpu-lyrics.md`. This file carries the full detail from the three Connect
agents: the read-only exploration (cluster→projection path), the implementation (Agent A, uncommitted diff), and the
log/dealer-archive investigation (Agent B). Line numbers are approximate against HEAD `02bf071b` unless marked (diff).

## 0. Identities and evidence

- Our device id `af11fca0…`; the iPhone `756f53b6…`. Process ids: 44168 (11:20–12:23), 41500 (14:1x–15:21:27),
  13896 (15:21:30→). The running build was published 11:18 from HEAD — none of Agent A's edits were running.
- Log `%LOCALAPPDATA%\Wavee\logs\wavee-20260911.log`. Keys: `[connect] put-state {Reason} active= track= pos=
  playing= paused= ctx= msgId= commandId= cluster=NNNB` (`DeviceStatePublisher.cs:318`), `connect.command.received
  /completed endpoint=…`, `server-clock …`; `[playback]` "another device became active - stopping stray local playback"
  (`PlaybackController.cs:855`), "another device became active — stopping local playback" (`:849`, the real takeover),
  "fast-start body ready … supplying to audio host" (`:691`), "continuation: prefetching autoplay" (`:2953`),
  `queue.recovery.seeded`, "play intent origin=…"; `[audio] seek deferred ms= session= bodyAttached=` /
  "deferred seek applying" (`FluentMediaAudioHost.cs:599/638`), `[posdiag]`.
- Dealer archive (inbound pushes only, since 14:57): `%LOCALAPPDATA%\Wavee\logs\dealer\dealer-20260911.bin` =
  concatenated JSON `{"headers":{"Transfer-Encoding":"gzip"},"payloads":["<base64>"],"uri":"hm://connect-state/v1/cluster",…}`
  + `.idx.ndjson` `{"t","typ","uri","handled","n","off"}`. Payload = base64 → gzip → protobuf `ClusterUpdate
  {1:Cluster{2:active_device_id, 3:PlayerState, 4:device map, 9:server_ts}, 2:update_reason, 4:devices_that_changed}`;
  `PlayerState.track = ProvidedTrack{1:uri, 2:uid, 3:metadata map<string,string>}`. A generic varint walker decodes
  it (Agent B's `decode.py` in the session scratchpad). **The announce-response cluster (`DeviceStatePublisher.cs:324`
  → `ClusterIngest.OnAnnounceResponse`) is NOT archived** — it is the frame that proves whether the server adopted us.

## 1. Cluster → projection path (exploration)

- Ingest: `ClusterMapper.cs:113` dealer topic `hm://connect-state/v1/cluster` → `OnEvent` (`:117-125`);
  `OnAnnounceResponse(byte[])` (`:129`) re-injects the PUT-state response as if it were a push (wired
  `LiveConnect.cs:133`, pushed from `DeviceStatePublisher.cs:324`) — **every put-state we send produces a second
  cluster fold (feed #2)**. `Apply` → `Map` → server-clock passive sample → `_devices.Update` → `_projection.OnCluster`.
- `ClusterMapper.Map` (`:15-64`) → `ClusterDelta` (`PlaybackProjection.cs:31-40`): ActiveDeviceId, Track,
  PositionAsOfMs, TimestampMs, ServerTimestampMs, NextTracks/PrevTracks, QueueRevision. `MapTrack` (`:66-84`) reads
  `title`, `artist_name`, `artist_uri`/`ProvidedTrack.ArtistUri`, `album_title`, `album_uri`, `duration`, images.
- Fold → bar: `PlaybackProjection.OnCluster` (`:735`) is the only remote writer; the bar reads via
  `PlaybackBridge.cs:984` → `:1367 CurrentTrack.Value = s.CurrentTrack`, `:1470 ActiveDeviceId.Value`, `:1473
  PushPosition`. One UI source — the flip is the projection publishing two alternating states.
- Writers of `_track`: cluster fold (`:790-796`, guarded by `localSessionOwns` at `:779` =
  `_hasLocalContext && (weActive || IsNullOrEmpty(c.ActiveDeviceId) || withinPendingWindow)`), local snapshot
  (`ApplyLocalSnapshot :595/:601`, **no guard**), local event (`:951/:964`, **no guard**), `SetLocalQueue` (`:566`,
  **no guard**), host signal (`:1036`, guarded in the controller at `PlaybackController.cs:3452` by
  `!RouteLocal() && !IsActiveOwner()`).
- Remote position: `:835-846` (server-side age + network age; `isNewTrack && PositionAsOfMs<=1000 ⇒ age 0`; during
  a flip every cluster push looks like a new track). Local position at publish: `PlaybackController.cs:3170-3174`
  `boundary` ⇒ pos 0 whenever the local current uri ≠ the projection's (the phone's) → "0:01 / 0:02".
- No `state_id`/`playback_id`/session-id guard anywhere; only the monotonic server-ts drop (`cluster.stale`,
  `:745-752`) and the 5 s local-command window (`:782 suppressPlayState`, `:824 inLocalWindow`).
- Continuation is a live local publish while remote-active: `:2953` → `EagerApplyContinuationAsync` →
  `ApplyContinuation` → `:3044 EmitSnap(QueueChanged)` → `Publish` → `ApplyLocalSnapshot`.
- Tests: `ConnectProjectionTests.cs` (`Trk`/`Cluster` builders, 15 tests listed in the exploration), `ConnectEndToEndTests`,
  `NowPlayingEnrichmentTests`, `QueueRecoveryTests`, `ConnectControllerTests` (takeover/regain at `:294-308`).
  **No test folds a local publish while a foreign device is active; `ClusterMapper` has no tests.**

## 2. Timeline reconstructed by Agent B (L = log, C = decoded cluster)

```
14:50:59.7  L  stray stop (StopStrayLocalHost)
14:57:35.7  L  continuation prefetch 6vfQ…; fast-start 6vfQ…; seek deferred ms=1137   (the PHONE's playhead on OUR track)
14:57:43.0  L  stray stop                                   ← C reason=2 active=PHONE
14:57:53.4  L  fast-start 6vfQ…; seek deferred ms=1788
14:58:02.8  C  reason=6 NEW_CONNECTION active="" devices={iPhone,Wavee} ps=phone's 40x5K8 paused@1788
14:58:03.4  C  reason=2 active=PHONE  → L stray stop
14:58:14.55 C  reason=2 active=PHONE pos=2578 paused
14:58:14.65 C  reason=1 DEVICES_DISAPPEARED active="" devices={Wavee only}, player_state still the phone's
14:58:14.65 L  fast-start 6vfQ…; seek deferred ms=2578    ← zombie local track at the phone's position
14:58:33/35/43 C reason 6/2/1 — the phone flaps in and out, active stays ""
15:02:23    L  put-state VolumeChanged active=False track=6vfQ pos=2578 ×3
15:20:31.9  L  [41500] user pressed play: play-intent raw=2578; put-state active=True 6vfQ
15:20:39.6  L  play intent origin=play-track-uri route=local 40x5K8 (search "lp lost on you"); put-state active=True
15:21:27    L  41500 exits.  15:21:30.8 13896 starts
15:21:31.94 C  reason=6 changedDevs=[us] active="" devices={Wavee} ps=OUR echo: 40x5K8 title='Lost on You' artist_name='LP' pos=22962 playing
15:21:31.94 L  queue.recovery.seeded (paused) positionMs=72777 staleActive=false; put-state NewConnection active=False
15:21:32.08 L  docked host … playing=…40x5K8 (recovery seed reached the bridge → RecomputeHasVideo)
15:21:32.11 L  put-state PlayerStateChanged active=True 40x5K8 pos=72777 playing=False msgId=2   ← SLOT STEAL
15:21:32.13 L  resolve/fast-start 40x5K8; seek deferred 72777 (paused reload = MaybeAutoReload on the response echo)
15:21:35.13 C  active=US devices={iPhone,Wavee} pos=72777 playing=0                            ← server adopted us
15:21:36.08 L  connect.command.received endpoint=pause sender=iPhone → Applied; put-state paused=True active=True
15:21:38.43 C  active="" ps=phone 40x5K8 pos=0 buffering;  15:21:38.94 C active=PHONE pos=0 playing → L stray stop
15:21:40.4  C  phone → 2t4RCW 'The First Time' (artist_uri 7AaGb…, NO artist_name)
15:22:59.52 L  play-intent raw=0; fast-start 40x5K8; put-state active=True pos=6448 playing=True   ← "transfer to this PC"
              (6448 = the PHONE's 2t4RCW position, extrapolated, applied to OUR 40x5K8; no transfer command sent)
15:23:00.02 C  still active=PHONE;  15:23:03.2 L local pause (inside the 5 s pending window)
15:23:50.39 L  outbound resume → iPhone: 403   (the bar routed the play button to the phone)
15:23:53.49 L  play-intent raw=10018 lastState=Paused (transfer-to-self again); put-state active=True pos=16 playing=True
15:23:50–15:26:28 C phone 2t4RCW playing/paused alternating 1 Hz, pos 57150→70549, active=PHONE throughout
15:23:53→15:28:05 L [posdiag] ticks (local host audibly playing 'Lost on You'); gapless re-arms at 15:27:57/15:28:05
15:26:28    C  last archived frame: phone paused pos=70549
```

## 3. Per-bug root cause, and what Agent A's diff does / misses

### 3.1 Flip loop
Cause: `OnProjectionChanged` (`PlaybackController.cs:836-864`) runs `MaybeAutoReloadOnOwnershipRegained(aid)` at
`:840` before the takeover/stray branches; `:877 if (aid != _ourDeviceId && !IsNullOrEmpty(aid)) return;` — an
**empty** aid passes; `:878` needs `_restorePendingLoad`, armed by `StopStrayLocalHost` (`:811-818`) →
`StopHostKeepingSession` (`:782-787`, the same latch the genuine takeover uses); `AutoReloadOnOwnershipRegainedAsync`
(`:892-910`) re-checks only the latch/uri/generation, never `RouteLocal()`; `LoadAndPlayCurrentAsync` (`:2585-2612`)
clears the latch, the `finally` (`:908`) clears `_ownershipReloadUri`, so the next stray stop re-arms both. The
position comes from `_projection.PositionMs` = the phone's playhead. The empty-aid branch (`:857-864`) also calls
`PublishInactiveOnWire`, producing another echo.
Agent A: `StopHostKeepingSession(reason, armRestore:false)` + `_strayStopped` (cleared by `SetActiveOwner(true)` /
`IsActiveOwner()`); `MaybeAutoReloadOnOwnershipRegained`: empty aid counts as ours only when `_ownsActivePlayback`;
`!RouteLocal() && !IsActiveOwner()` bail in `AutoReloadOnOwnershipRegainedAsync` (under lock), `SupplyBodyWhenReadyAsync`,
`MaybeStartContinuationFetch`; NOT in `LoadAndPlayCurrentAsync` (would break
`InboundCommand_AlwaysLocal_EvenWhenClusterShowsAnotherActive`; `HandleInboundResumeAsync` reaches it without
`_connectOriginatedPlayback`). Projection: `suppressNowPlaying` in `ApplyLocalSnapshot`/`OnEvent` withholds
`_track/_posMs/_isPlaying` when a foreign device is active and kind ∉ {Ended, BecameInactive}; one-shot
`projection.local.suppressed`.
Residual (B): `RouteLocal(aid)` (`:765`) returns **true for an empty id**, so the new guards are no-ops in the exact
DEVICES_DISAPPEARED windows; `_restorePendingLoad` is still armed by `DeactivateIfActiveOwner` (`:830`), recovery
(`:2337`), `:2184`, `:2788` → a genuine takeover followed by a phone flap reproduces the loop; the projection still
folds a foreign player_state of an empty-active frame and clamps `_isPlaying=false` (`:843`) while keeping the phone's
`_posMs`, which is where the reload takes its position. The `ApplyLocalSnapshot` suppression also breaks transfer-in
while a foreign id is still in `_activeDeviceId` for up to 5 s (the pending window is only consulted in `OnCluster`).

### 3.2 "LP / LP", "Damiano David / Damiano David"
Wire: phone frames for `2t4RCW` carry `title='The First Time'`, `artist_uri`, no `artist_name`; for `40x5K8`
`title='Lost on You'` (+ our own echo adds `artist_name='LP'`). library.db `entity` payloads (zstd) hold the correct
titles/artists. Mapping keeps the wire title verbatim (`ClusterMapper.MapTrack`, `NowPlayingProjection.MapTrack:1169`,
`PlaybackSession.TrackFromRemote:975`, `TrackFromWireMetadata` `PlaybackController.cs:3973`, `StoreEntityMerge.Track`
`Store.cs:134`, enrichment merge `PlaybackProjection.cs:929` `Title = TitleMissing ? e.Title : cur.Title` with
`TitleMissing` = null/empty/uri). Every render surface reads `t.Title` (`PlayerBar.cs:699`, `StageIdentity.cs:302`).
**No path in HEAD writes an artist name into Title; not reproduced from code.** Unexhausted suspects: display fallback
when `Title` is empty (queue rows in the archive often carry only `context_uri/entity_uri`), enrichment filling
`Artists` on an empty-title row then the thin-track ladder marking it non-thin (`:897-901`), `TrackFromWireMetadata`
for set_queue rows, station-context continuation rows, the 11:18 build's uncommitted state (gone).
Agent A: `RepeatsTitleAsArtist` blanking in `MapTrack` (+ `cluster.title-as-artist` log) and `TitleEqualsAnyArtist` in
the enrichment merge, plus `ClusterMapperTests`, `NowPlayingEnrichmentTests.WireTitleEqualsArtistName_…`. Both are
**dead code for these frames** (title ≠ artist_name) and the projection variant would rename a legitimately
self-titled track. Recommended instead: an always-on `nowplaying.identity` line in `FireChanges` whenever `Title`
equals any `Artists[i].Name` or is empty (uri + source of the last `_track` write: cluster/local/enrich/override), and
archive the announce response.

### 3.3 Audio here while the bar says "Playing on iPhone"
Device picker "This computer" = `LocalAudioDeviceService.SelectAsync` (`:108-120`) → `TransferToAsync(us)` →
`ResumeCurrentLockedAsync` (`PlaybackController.cs:1379-1390, 2170`): a local resume that never asks the server for
the slot (comment: the transfer endpoint 400s for self). Loads our session current (40x5K8) at
`_projection.PositionMs` (the phone's 6448 ms of 'The First Time') and publishes `is_active=true`. The server kept the
phone (`active=PHONE` at 15:23:00.02, 15:23:50.48): `DeviceStatePublisher.cs:185` sets `_startedPlayingAtMs` only
when 0, it was set at 15:21:32 by the recovery publish and only reset on `Ended/BecameInactive` (`:197`;
`PublishInactive :234` does not reset) → older than the phone's → not adopted (consistent, unprovable without the
response frame). Meanwhile `Publish(Started)` (`:3196`) sets `_ownsActivePlayback=true` and `OnProjectionChanged`
demotes only on an active-id transition (`:862 if (aid == _lastActive) return;`) — none occurs → `IsActiveOwner()`
stays true, `RouteLocal()` false, host keeps playing. 15:23:50 the bar's play button routes to the phone (403)
because `ResumeAsync` reads `ActiveDeviceId` while the picker path ignores it. Agent A: **nothing** covers this.

### 3.4 PLAY glyph while audio plays; lyrics tick while "paused"
`OnHostSignal` (`PlaybackProjection.cs:1099-1151`) sets `_isPlaying = s.IsPlaying`, `_posMs = host`, fires `Changes`
**only when `effPlaying != _lastPubPlaying`**, a memory updated only there. The phone's 1 Hz frames fold
`_isPlaying=false` and fire (`:843, :876`); the next 5 Hz host tick restores true but the memory is still true → no
fire → bridge keeps `IsPlaying=false` (PLAY glyph, `PlayerBar.cs:373 playing ? Pause : Play`) while
`_positionTicks.OnNext(clampedPos)` (`:1150`) pushes the LOCAL host's playhead ('Lost on You', clamped to `_track`'s
duration = 'The First Time' 3:37 → "2:49 / -0:48") every 200 ms → `PushPosition` → `PositionMs`. The controller's
`OnHostSignal` guard (`:3501`) does not stop it because `IsActiveOwner()` is stuck true (3.3). `LyricsView.OnFrame`
(`:2016-2021`) is correct: with `IsPlaying=false` it pins `nowMs = auth` and follows the advancing `PositionMs`.
Agent A: **nothing**.

### 3.5 Launch slot steal (15:21:32)
Recovery seeds paused without announcing (`RunSessionRecoveryAsync :2342`), but the seed reaches
`PlaybackBridge.RecomputeHasVideo` (`:1290-1304`): the 40x5K8 video association trips `_connectVideoFacts.Observe` →
`RepublishConnectState` → `DeviceStatePublisher.PublishStateChanged` (`:225-232`) →
`PublishAsync(PlayerStateChanged, isActive: true, force: true)` — literal `true`, `OwnsSession()` not consulted,
`_ownershipRetired` false at launch. Server adopts us, phone gets kicked, sends `pause`, re-takes from 0; the response
echo (`active=US`) triggers `MaybeAutoReloadOnOwnershipRegained(us)` → the paused reload at 15:21:32.13. Agent A:
**nothing**.

### 3.6 Video duration adopted from the previous video
`LiveConnect.cs:369-375` `_onVideoDurationKnown = (key, ms)` applies any key to the current track. Agent A: new pure
`Backend/VideoDurationAdoption.ShouldAdopt(currentKey, eventKey)` wired against `_videoHost.CurrentSourceKey`, with
`VideoDurationAdoptionTests`. Keep this part.

## 4. Agent A's diff — file summary (uncommitted; `git diff` in the worktree)
- `Backend/PlaybackController.cs`: `StopHostKeepingSession(string reason, bool armRestore = true)`; `_strayStopped`
  (currently unused → **CS0414 warnings-as-errors build break**); `MaybeAutoReloadOnOwnershipRegained` empty-aid rule;
  `RouteLocal()/IsActiveOwner()` bails + Info logs in `AutoReloadOnOwnershipRegainedAsync`, `SupplyBodyWhenReadyAsync`,
  `MaybeStartContinuationFetch`; NOTE comment on `LoadAndPlayCurrentAsync`.
- `Backend/PlaybackProjection.cs`: `suppressNowPlaying` in `ApplyLocalSnapshot`/`OnEvent`; `_lastSuppressedForeignId`
  one-shot log; `TitleEqualsAnyArtist` in the enrichment merge.
- `SpotifyLive/ClusterMapper.cs`: `RepeatsTitleAsArtist` (public) blanks title+artist; `ClusterIngest` one-shot
  `cluster.title-as-artist` log.
- `SpotifyLive/LiveConnect.cs`: duration event guarded by `VideoDurationAdoption.ShouldAdopt`.
- New: `Backend/VideoDurationAdoption.cs`; tests `ClusterMapperTests.cs` (**needs `ClusterMapper.cs` source-included
  in `Wavee.Tests.csproj` — the agent says it added the include; the build still reports CS0103 `ClusterMapper`, verify**),
  `VideoDurationAdoptionTests.cs`, additions to `ConnectControllerTests` (`ForeignDeviceActive_StrayStop_ThenEmptyAidEcho_DoesNotReload`,
  `RegainOwnership_StillReloads`), `ConnectProjectionTests` (`LocalSnapshot_WhileForeignDeviceActive_DoesNotOverwriteRemoteTrackOrPosition`,
  `LocalEnded_WhileForeignDeviceActive_StillApplies`), `NowPlayingEnrichmentTests` (`WireTitleEqualsArtistName_ReplacedByCatalogTitle_OnEnrichment`).
Recommendation: keep 3.6 and the two projection/controller tests as regression scaffolding; drop the title heuristics;
replace the guards with the ownership design below rather than layering more predicates.

## 5. Root-cause design (Agent B; a second high-reasoning verification pass was started and killed for usage)

a. **`Backend/PlaybackOwnership.cs`** (engine-free): `Fold(clusterActiveId, clusterServerTs, ourId, localClaimTs)
   → Owner { Us, Foreign(id, name), Nobody }`. A local claim (explicit play, transfer-to-self, inbound transfer) stamps
   `localClaimTs`; a cluster fold revokes it only when `serverTs > localClaimTs` and names a foreign id (replaces the
   5 s wall-clock `LocalOwnershipPending`). Delete `_ownsActivePlayback`, `_strayStopped`, `IsActiveOwner`,
   `SetActiveOwner`, `RouteLocal(aid)`'s three-way rule, `_lastActive` transition logic; controller and projection read
   the same `Owner`. `Owner == Foreign` ⇒ host must not run: stop on the fold itself (not on a transition),
   `OnHostSignal` drops everything but `Ended/Error`, `ApplyLocalSnapshot/OnEvent` fold nothing into now-playing,
   reload/continuation/`SupplyBody` all read the predicate. `Nobody` is not ownership: never reloads, never publishes
   `is_active=true`; only a user intent does.
b. **`is_active` has one writer**: `DeviceStatePublisher` derives it from `Owner == Us` (drop the `isActive` parameter
   of `PublishAsync`; `PublishStateChanged` cannot claim); `_startedPlayingAtMs` stamped on every local claim, reset on
   demotion so the server's newest-starter rule can adopt us.
c. **Transfer-to-self**: local claim + `PutState(PLAYER_STATE_CHANGED, is_active=true, started_playing_at=now,
   has_been_playing_for_ms=0)`; the response cluster is authoritative — if it still names a foreign device: stop the
   host, log `connect.claim.rejected`. Bar play button and picker both route through `Owner`.
d. **Play-state publishes on every disagreement**: the last *published* (IsPlaying, IsBuffering, CurrentTrack) tuple
   lives in `FireChanges`; any fold that changes it fires; position ticks only while `Owner == Us`.
e. **Self-echo recognition**: `ClusterDelta.Origin = SelfEcho` for the announce response (+ `devices_that_changed`);
   a self-echo never revokes a claim and never counts as Foreign; `DEVICES_DISAPPEARED` keeps the previous owner's
   player_state as a stale viewer snapshot (no reload, no clamp-to-paused publish). Archive the announce response in
   the dealer archive (`typ:"announce-response"`).
f. **One identity rule** `TrackIdentityMerge.Apply(wire, catalog)`: wire title unless `TitleMissing`; artists/album/art
   from the catalog; no artist-name heuristics. Add the `nowplaying.identity` evidence line.
g. **Diagnostics (always-on)**: `connect.owner` (state transitions, reason, server ts, claim ts), `connect.putstate`
   (isActive, startedPlayingAt, hasBeenPlayingForMs, origin), `connect.echo` (announce response: active id, adopted?),
   `nowplaying.identity`, `playback.reload` (origin + owner state for every local load), `projection.publish`
   (IsPlaying/track/pos deltas + writer); Diagnostics-page "Connect state" card (Owner, claim ts, last cluster server
   ts, last put-state msgId/isActive).

## 6. Tests to add
- `PlaybackOwnershipTests`: claim/revoke matrix (Nobody / Us / Foreign × claim older/newer than serverTs × self-echo).
- `ConnectProjectionTests`: `OnCluster_ForeignActive_HostTickDoesNotMovePositionOrPlayState`;
  `OnCluster_ForeignPaused_ThenHostTick_PublishesIsPlayingFalse_AndPositionFrozen` (one Changes, position identical over
  3 s of ticks); `OnCluster_DevicesDisappeared_EmptyActive_KeepsForeignSnapshot_NoLocalOwnership`;
  `OnCluster_SelfEcho_DoesNotRevokeLocalClaim`; `OnCluster_ForeignWithNewerServerTs_RevokesLocalClaim`;
  `Pos_RemotePause_StopsAgeing_EvenAfterLocalTicks`.
- `ConnectControllerTests`: `ForeignActive_NoTransition_StraySessionStart_IsDemotedOnNextFold`;
  `TransferToSelf_WhileForeignActive_StampsStartedPlayingAt_AndPublishesActiveOnce`;
  `TransferToSelf_ResponseStillForeign_StopsHost_LogsClaimRejected`; `BadgeRepublish_WhileNotOwner_PublishesIsActiveFalse`;
  `EmptyActiveAfterStrayStop_NeverReloads`; `DeactivateThenPhoneFlap_DoesNotReload`; `PlayButton_And_DevicePicker_RouteThroughSameOwner`.
- `NowPlayingEnrichmentTests`: `WireTitleKept_WhenArtistNameMissing_ArtistsFilledFromCatalog`;
  `SelfTitledTrack_TitleNotReplacedByCatalog`; `IdentityLog_FiresWhenTitleEqualsArtist`.
- `QueueRecoveryTests`: `RecoverySeed_DoesNotPublishOnWire_EvenWhenVideoBadgeLands`;
  `ReplaceFromCluster_KeepsWireTitleAndArtistUri_NoArtistName`.
- Replay fixtures from the decoded archive frames (14:57–15:26) for the six incidents.

## 7. Suggested implementation order (disjoint file groups)
1. `PlaybackOwnership` + `PlaybackProjection` fold/publish rules (+ ConnectProjectionTests).
2. `PlaybackController` (delete the duplicated predicates, route every local load through `Owner`, transfer-to-self
   claim) + `LocalAudioDeviceService` (+ ConnectControllerTests).
3. `DeviceStatePublisher` (`is_active` from Owner, started_playing_at stamping, echo tagging) + `ClusterMapper`/`ClusterIngest`
   (self-echo origin, `devices_that_changed`) + dealer archive of the announce response.
4. `PlaybackBridge` (badge republish never claims; optimistic toggle reverted by the next push) + diagnostics lines +
   Diagnostics-page card.
5. Identity: `TrackIdentityMerge`, remove `RepeatsTitleAsArtist`/`TitleEqualsAnyArtist`, `nowplaying.identity`.
Risks: inbound commands addressed to us during a stale foreign cluster must still play (existing test); recovery must
never claim; the 5 s pending window semantics are replaced by server-ts vs claim-ts — verify against the archived
15:21–15:24 frames.
