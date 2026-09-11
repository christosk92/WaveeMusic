# Handoff — 2026-09-11 session (0.2.9 worktree `C:\WAVEE\wavee-0.2.9`, branch `release/0.2.9-perf`)

Everything below is **uncommitted** in the worktree unless stated. The user's own WIP (NPV player styles, scroll
probe tooling, `Program.cs`, `en-US.json`, `CHANGELOG.md`, …) is interleaved in the same tree — treat foreign hunks as
theirs. Commits are the user's; each needs `Fixes #n` matching the CHANGELOG bullet (release gate).

## 1. State of the tree

| Batch | Status | Issues |
|---|---|---|
| ylpin pin sync ported (`git cherry-pick cfc64300` → commit **02bf071b**) | committed | #102 |
| Liked Songs = `spotify:collection`; folder pins ↔ `spotify:folder:HEX`; unrepresentable pins preserved; pinned items hidden from every list/tree/rail/shortcut when a Pinned section exists (`SidebarRowPlanner.HiddenByPin`); missing-folder row | built + 7800 tests green (before later batches) | #102 |
| CLI probes: echo in Release (`Diagnostics/CliRun.cs`), `ExitCli` flush, `clearStoredOnReject:false` for probes, `added_at` in `--spotify-collection`, `.claude/skills/wavee/probes.md` | green | #131 |
| Storage cards column wrapper; Liked route pin name (`SidebarPinSync` routeTitle + `EntryRow` fallback); `SidebarCover.Liked` sized box; Shortcuts preset `Subtitles:false` (40 DIP); collaborators face pile reads the Loadable + keyed embeds; expander Added-by chip (not clickable — no user route exists); Shortcuts band emits no header | Debug+Release build green, full suite 7805/7807 (2 known flaky, see §6) | #132 #133 #134 #135 (+#102) |
| Lyrics decoy defenses (`LyricsTiming.ExceedsTrackDuration/HasUniformLineDurations`, `FetchOne` Miss, reranker `verified` needs text>0 with a reference, unsynced-reference neutral timing, background pass replaces unverified winner, Musixmatch `message.header.status_code` + 12 h token TTL + drop-on-≠200, fixture `musixmatch-decoy-sorry-seems.lrc`) | compiles; **tests not yet run** | #137 |
| Lyrics blur strength slider (`appearance.lyrics.blurStrength`, `LyricsBlurPolicy` auto = 40 on `GpuProfile.IsWeak` else 100; `DofWriteEps` 0.5; halo never nested in a DoF layer; Settings › Appearance › Lyrics row + `SettingsGlyphs` "Filter") | compiles; **tests not yet run** | #141 |
| Engine (`C:\WAVEE\fluent-gpu`, on top of the user's large animation-rework WIP): `ScrollBarChrome.NeedsFrame` (+ `_armedSinceTick`), `ScrollKernel.WakeActiveCount` (excludes Parked), `ScrollCommandPort` coalesces identical `SetFrame`, `SceneRecorder.BlurSuppressedByScrollCount` aliased to the real hold count, **one-line wiring at `AppHost.cs:2215`** | Debug+Release green, VerticalSlice **ALL CHECKS PASSED (1477)**, 11 chrome/kernel/port tests green | #136 |
| Connect guards (Agent A): `StopHostKeepingSession(armRestore)`, `_strayStopped`, empty-aid rule, `RouteLocal/IsActiveOwner` bails in `SupplyBodyWhenReadyAsync`/`MaybeStartContinuationFetch`/reload task, projection `suppressNowPlaying`, `ClusterMapper.RepeatsTitleAsArtist` blanking, `TitleEqualsAnyArtist`, `VideoDurationAdoption`, tests | **DOES NOT COMPILE** (see §4) and is judged partial/band-aid — user asked for a root-cause design first (§3) | #138 #139 #140 |
| CHANGELOG bullets for #131–#141 written; plan doc `pin-spotify-sync-implementation.md` brought to 0.2.9 and updated; guide + pitfalls updated; memories saved | done | |

`[wake]` / `blurHeld` per-term attribution in `WakeDiagnostics.cs` and the minimized-turn bucket were **not** done
(those files carry the user's WIP; only the one line in `AppHost.cs` was touched).

## 2. Root causes established from the logs (pid 44168 11:20–12:23, pid 41500 14:1x–15:21, pid 13896 15:21→)

- **GPU 71 % / lyrics stepping / video stutter**: the render loop never idled since launch (`[wake] streak↑, idleAgo`
  never reset). `scrollAnim` held every iteration: a pointer resting over a scrollable view keeps a scrollbar chrome
  row forever, parked bodies count as active, layout re-posts an identical `SetFrame` each frame. A record-only frame
  still re-records and submits when any pixel moves. Lyrics: one self-blur group per visible line (canvas-sized RT +
  2 full-res Gaussian passes), glow nested in DoF re-Gaussians every hand-off, `DofWriteEps` 0.1 vs pin bucket 0.5.
  Video presents via DirectComposition — collateral. No audio xruns. Fixed by the engine + blur batches above.
- **Lyrics decoy**: Musixmatch anti-scraping payload (206 uniform 4 s nonsense lines to 13:56 on a 3:30 track) won
  because an ISRC match is verified by construction, tier beats score, "too few pairs" timing = neutral 0.6, background
  pass only replaces by richer sync class, provider ignores `message.header.status_code` (401 captcha / 402 quota
  arrive as HTTP 200) and caches its token forever. Web research (Sept 2026): every client that survives treats the
  token as short-lived (10 min cache, 10 s backoff on 401), spicetify gave up on auto-handling, Musixmatch's web API
  moved to HMAC-signed requests in Feb 2026 (Strvm/musicxmatch-api #16); no one validates lyric plausibility.
- **Connect flip-flop (14:50–15:00)**: iPhone connection flaps (`DEVICES_DISAPPEARED` → `active_device_id=""` →
  `NEW_CONNECTION`); each empty fold passes `MaybeAutoReloadOnOwnershipRegained("")`, `_restorePendingLoad` armed by
  the stray stop → `LoadAndPlayCurrentAsync` at the PHONE's playhead applied to OUR track (`seek deferred
  ms=1137/1788/2578`) → next `active=PHONE` fold stops it as stray → repeat every ~10 s.
- **"LP / LP" / "Damiano David / Damiano David"**: **NOT** the cluster. Decoded frames carry `title='Lost on You'` /
  `'The First Time'` + `artist_uri`, no `artist_name`; library.db has correct titles; every merge keeps the wire title
  unless `TitleMissing`. No path in HEAD writes an artist name into `Title` — mechanism unknown; `RepeatsTitleAsArtist`
  (d903cef9, not on this branch) and `TitleEqualsAnyArtist` are dead code for these frames, and the projection variant
  would wrongly rename a self-titled track. Needs an evidence line (`nowplaying.identity`), not a heuristic.
- **Audio here while bar says "Playing on iPhone" (15:22:59, 15:23:53)**: device picker "This computer" →
  `TransferToAsync(us)` → `ResumeCurrentLockedAsync` — a local resume that never claims the slot; server keeps the phone
  (`_startedPlayingAtMs` stamped once at 15:21:32 and never reset → older than the phone's); `_ownsActivePlayback`
  set true by `Publish(Started)`, demoted only on an active-id TRANSITION (`if (aid == _lastActive) return;`) — none
  occurs → host keeps playing; the bar's play button routes to the phone (403). Two ownership authorities disagree.
- **PLAY glyph while playing + lyrics ticking while paused**: `OnHostSignal` fires `Changes` only when
  `effPlaying != _lastPubPlaying` (memory updated only there); the phone's 1 Hz frames fold `_isPlaying=false` and
  fire; the next host tick restores true without firing → bridge stays `IsPlaying=false` while `_positionTicks` push
  the LOCAL host's playhead every 200 ms → LyricsView follows PositionMs (its clock is correct given a lying source).
- **Launch slot steal (15:21:32)**: session recovery seeds paused without announcing, but the seed reaches
  `PlaybackBridge.RecomputeHasVideo` → `RepublishConnectState` → `DeviceStatePublisher.PublishStateChanged` →
  `PublishAsync(PlayerStateChanged, isActive: **true**, force: true)` (literal, `OwnsSession()` not consulted) → server
  adopts us, kicks the phone, phone re-takes from 0; the response echo triggers a paused reload.
- **Video duration adopted from the previous video** on host switch (`LiveConnect._onVideoDurationKnown` accepts any key).

Decoder + dumps: `scratchpad\decode.py`, `clusters.txt`, `timeline.txt`, `inprogress.diff` (session temp; copy them if
needed). Dealer archive (inbound only): `%LOCALAPPDATA%\Wavee\logs\dealer\dealer-20260911.{bin,idx.ndjson}`. The
announce-response cluster is **not** archived — the one frame that proves adoption.

## 3. Root-cause design (from the investigation; the user wants this reviewed BEFORE code)

a. **One ownership authority** — engine-free `Backend/PlaybackOwnership.cs`:
   `Fold(clusterActiveId, clusterServerTs, ourId, localClaimTs) → Owner { Us, Foreign(id,name), Nobody }`. A local
   claim (explicit play / transfer-to-self / inbound transfer) stamps `localClaimTs`; a cluster fold revokes it only
   when `serverTs > localClaimTs` and names a foreign id (replaces the 5 s `LocalOwnershipPending` wall-clock window).
   Delete `_ownsActivePlayback`, `_strayStopped`, `IsActiveOwner`, `SetActiveOwner`, `RouteLocal(aid)`'s three-way
   rule, `_lastActive` transition logic. Controller and projection read the same `Owner`. `Owner==Foreign` ⇒ the host
   must not run: stop on the fold itself (not on a transition); `OnHostSignal` drops all but `Ended/Error`;
   `ApplyLocalSnapshot/OnEvent` fold nothing into now-playing; reload/continuation/`SupplyBody` read the predicate.
   `Nobody` is not ownership: never reloads, never publishes `is_active=true`; only user intent does.
b. **`is_active` has one writer**: `DeviceStatePublisher` derives it from `Owner==Us` (remove the `isActive` param from
   `PublishAsync`); the video-badge republish can never claim; `_startedPlayingAtMs` stamped on every local claim,
   reset on demotion.
c. **Transfer-to-self claims properly**: local claim + put-state `is_active=true, started_playing_at=now,
   has_been_playing_for_ms=0`; the response cluster is authoritative — if it still names a foreign device, stop the host
   and log `connect.claim.rejected`. Bar play button and picker both route through `Owner`.
d. **Play-state publishes on every disagreement**: last *published* tuple lives in `FireChanges`; any fold changing
   `IsPlaying/IsBuffering/CurrentTrack` fires; positions pushed only while `Owner==Us`.
e. **Self-echo recognition**: `ClusterDelta.Origin = SelfEcho` for the announce response (+ `devices_that_changed`);
   a self-echo never revokes a claim and never counts as Foreign; `DEVICES_DISAPPEARED` keeps the previous owner's
   player_state as a stale viewer snapshot (no reload, no clamp-to-paused publish). Archive the announce response.
f. **One track-identity rule** `TrackIdentityMerge.Apply(wire, catalog)`: wire title unless `TitleMissing`; artists /
   album / art from the catalog; **no artist-name heuristics** (drop `RepeatsTitleAsArtist`, `TitleEqualsAnyArtist`).
g. **Diagnostics (always-on)**: `connect.owner` (transitions, reason, server ts, claim ts), `connect.putstate`
   (isActive, startedPlayingAt, hasBeenPlayingForMs, origin), `connect.echo` (announce response: active id, adopted?),
   `nowplaying.identity` (when title==artist or title empty: uri + writer), `playback.reload` (origin, owner state),
   `projection.publish` (IsPlaying/track/pos deltas + writer); a Diagnostics-page "Connect state" card.
h. **Tests**: `PlaybackOwnershipTests` (claim/revoke matrix); `ConnectProjectionTests` (foreign active: host tick moves
   nothing; foreign paused then host tick → IsPlaying false + frozen position; devices-disappeared keeps foreign
   snapshot; self-echo never revokes; newer server ts revokes); `ConnectControllerTests` (stray session under foreign
   active demoted on next fold; transfer-to-self stamps started_playing_at once; response-still-foreign stops host;
   badge republish while not owner publishes active=false; empty-active after stray stop never reloads; deactivate +
   phone flap never reloads; play button and picker share Owner); `NowPlayingEnrichmentTests` (wire title kept when
   artist_name missing; self-titled track not renamed); `QueueRecoveryTests` (recovery seed never publishes even when
   the video badge lands). Replay fixtures from `clusters.txt`.

A second Fable design pass (verify A+B, scout more, refine c/d/e) was started and killed for usage; re-run it with the
prompt in this session's transcript if wanted.

## 4. Immediate next steps

1. Decide on §3 with the user. Then either revert Agent A's Connect diff (`git checkout -- src/apps/Wavee/Backend/PlaybackController.cs src/apps/Wavee/Backend/PlaybackProjection.cs src/apps/Wavee/SpotifyLive/ClusterMapper.cs src/apps/Wavee/SpotifyLive/LiveConnect.cs`, and delete `src/apps/Wavee.Tests/ClusterMapperTests.cs`, keep `VideoDurationAdoption*.cs`) or finish it. Current compile errors: `ClusterMapperTests.cs` needs `ClusterMapper.cs` source-included (the agent says it added the include — verify), `PlaybackController._strayStopped` unused (CS0414, warnings-as-errors).
2. `dotnet build Wavee.slnx` Debug + Release (`-o` scratch) and the full suite; expect the lyrics/blur tests to need a
   pass (they were never run).
3. NativeAOT arm64 publish (`PATH` must include `C:\Program Files (x86)\Microsoft Visual Studio\Installer` for
   `vswhere.exe`): `dotnet publish src/apps/Wavee/Wavee.csproj -c Release -r win-arm64 -p:NuGetAudit=false -p:NativeDebugSymbols=true -p:DebugType=portable`.
4. Manual checks: GPU with lyrics open + cursor resting (expect well under 30 %, `[wake]` idle resets); blur slider
   0/40/100; "Sorry Seems To Be The Hardest Word" source report rejects musixmatch as decoy; Connect scenarios after §3.
5. Engine: the wake-term wiring is a one-line change inside the user's `AppHost.cs` WIP — tell them; `WakeDiagnostics`
   attribution still open (#136).

## 5. Web research on Musixmatch decoys (Sept 2026)
Throttle signal is `message.header.status_code 401` + `hint: captcha|renew` inside HTTP 200; clients cache the token
≤10 min and back off 10 s; spicetify pins `x-mxm-app-version: 10.1.1`, a `Musixmatch/2025120901 CFNetwork` UA and
`x-mxm-token-guid`, sends `track_spotify_id` + `q_duration`; Strvm/musicxmatch-api signs URLs (HMAC-SHA256 over
URL+date, secret scraped from the web JS) since Feb 2026; beets warns on marker strings. Suggested extras beyond what
landed: 10 s cooldown after `captcha`, `renew` ⇒ drop token + refetch once, the identity headers, `track_spotify_id`.

## 6. Known flaky full-suite tests (pass in isolation)
`EntityResidencyTests.ColdFallback_IsUnconditional_NotGatedOnEviction`, `CoverColorPlaneTests.FailedBatch_IsRetriedByTheNextRender`.

## 7. Session gotchas recorded in memory
- Never run `--spotify-*` probes from an agent shell against the live profile (credential wipe before #131).
- `vswhere.exe` PATH for NativeAOT publish from Bash.
- For GPU/stutter reports read `[wake]` and `frame.slow … blurGroups=` first.
