# Handoff — 2026-09-18 evening: playback end / Connect fixes, podcast rework, corrupt store

One session, four threads. Worktree `C:\WAVEE\wavee-0.3` (`feat/0.3-structure` @ `02f22cce` + a large uncommitted
tree — the library rework, video work and tonight's changes all sit in it together), engine `C:\WAVEE\fluent-gpu-pin`.
**The daily-driver app is the Release arm64 publish of THIS worktree** (the startup log line's `iconFont=` path says so),
not `C:\wavee\WaveeMusic` main. Logs: `%LOCALAPPDATA%\Wavee\logs`.

> **STATE OF THE TREE: NOTHING FROM TONIGHT HAS BEEN BUILT OR TESTED.** Three Sonnet agents wrote code by reading
> only; the orchestrator's first build was stopped by the user. The user published a Release build at 19:44 *while the
> agents were mid-edit*, so the currently running binary contains a partial, unknown subset of the changes below.
> First action next session: one Debug build + one Release build of `src/apps/Wavee.Tests/Wavee.Tests.csproj`, the
> test run, and the engine gates in `fluent-gpu-pin` (`--blame-hang-timeout`).

## The documents (all in `docs/plans/wavee/`)

| File | What |
|---|---|
| `playback-end-and-connect-fix-plan-20260918.md` | the approved plan: the diagnosis (Part 1) + the podcast prototype brief (Part 2). Copy of `~/.claude-work/plans/explain-why-palyback-is-indexed-cat.md` |
| `podcast-show-episode-mica.html` | the approved podcast prototype — published at https://claude.ai/artifact/92Ly7eaziCbWB4r1x4sgYU |
| `podcast-show-rework-implementation.md` | the full technical build plan for the podcast show page + episode page (waves P1–P10, real code, verified names, wire shapes) |
| `cache-integrity-and-playlist-diff-implementation.md` | **the database + playlist `/diff` + request-shape plan** (waves D0-D5): schema-named cache file opened after the instance gate, rename-first delete, mid-session recovery; playlists/rootlist/revision persisted atomically; op replayer; per-playlist dealer pushes; sidebar stops forcing full playlist reads; per-tick `Fetch.Drain` + the Online gate. Root cause of the 09-18 corruption is in its §1 |
| `handoff-20260919-complete-prompt.md` | **the consolidated handoff prompt for a fresh session** (playback, Connect, store, wire log, playlist `/diff`, podcasts) |
| `C:\WAVEE\wavee-captures\playlist4-2026-09\README.md` | (outside the repo) the four decoded Fiddler captures: decoders, decoded trees, ~825 protobuf bodies, which fixture proves which rule |
| this file | status, what is unverified, what is next |

---

## 1. Playback stuck at 4:04 + Connect invisibility

### Diagnosis (proven from logs, each link read in source — details in the plan doc)

1. Wavee mirrored a remote Connect device; "Closer" was the mirrored row for 2 h 23 min with no cluster update.
2. `DoTick` folded the extrapolated position every second with no owner test; `MirrorRemote` stamped the mirror with the
   local clock; `Position()` clamps to the duration ⇒ the stale mirror ratcheted to exactly 244 960 ms.
3. Play pressed ⇒ load `fromMs` = the full duration ⇒ `audio.seek.short … eof=1`.
4. Nothing was prepared: after adopting a cluster the session has no `Cursor`/`Context` (`DecideSeed` refuses to
   re-seed a non-`Unknown` queue) ⇒ `NaturalNext` returns nothing; the endgame asked once (a latch) and never again.
   Same cause for "Playing from Liked Songs" over 49 daylist rows.
5. **`Ended` was unreachable on a real WASAPI device**: with zero voices the mixer kept rendering silence into the
   sink, so `WritableFrames >= CapacityFrames` never held. 53/53 transitions that day were crossfades, 0 `Ended`. It
   had been fixed only for `--fake`.
6. Connect: 29 AP resets/day; any drop (AP *or* dealer) closes both sockets and wipes the connection id; announces
   made while the id is empty were silently dropped; `PutSent` was posted after the 2xx so a failed PUT could never
   settle the claim; a new connection id while Online was never announced; no ownership logging in the file.

### Written tonight (UNBUILT, UNTESTED)

| Item | Files | Notes |
|---|---|---|
| **A1** engine never renders a drained mixer's filler; pure `DrainVerdict.Decide` | `fluent-gpu-pin`: `Media/Playback/Audio/PcmAudioPlayer.cs`, **new** `DrainVerdict.cs`, **new** `FluentGpu.Engine.Tests/DrainVerdictTests.cs` | short-circuit gated to `_transportPhase == 0` so a pause fade over an empty mixer still completes. New RT-feed + buffered-sink behavioural test |
| A1 app side: `--fake` workaround deleted | `Playback/Playback.Audio.cs` (`PacedSilentEndpoint.Follow`/`IsTrailingFillerLocked`/`FollowSilentSession`), `Wavee.Tests/PlaybackAudioTests.cs` (test retargeted) | |
| **A5** per-tick `EndgamePlan.Decide` (re-asks every 3 s), `[gapless] ask n=` log | **new** `Playback/Playback.Endgame.cs`, `Playback.Audio.cs`, **new** `PlaybackEndgamePlanTests.cs` | orchestrator fix applied: the arm line is still owed on the commit tick (8.5 s fade arms and commits on the same tick) |
| **A2** `MirrorSnapshot.Project` (cluster's own clock, stale ⇒ Paused); `DoTick` folds only when `Owner.Us` | `Playback/Playback.cs`, **new** `MirrorSnapshotTests.cs` | |
| **A3** `ResumeStart.For` (≤ 1.5 s from the end ⇒ next row / 0), `SeekTarget.Clamp` (750 ms tail guard) | `Playback.cs`, **new** `ResumeStartTests.cs`, `SeekTargetTests.cs`; `PlaybackStepTests.cs` expectations updated | the two constants are judgment calls |
| **A4** takeover adopts context + re-seeds queue/cursor; `DecideSeed(takeover)` | `Playback.cs`, `Playback.Host.Remote.cs`, `Entities/Queue.cs`, `QueueSeedTests.cs`, `PlaybackStepTests.cs` | **host hooks added by the orchestrator** in `Playback.Host.cs`: `RemoteState` now carries `d.ContextUri`; `Execute()` runs `SeedQueueFromCluster(takeover: true)` before Stop/Load |
| **B1** `PutSent` before `Api.Send`; `Flush` serialised + re-armed | `Spotify/Spotify.Connect.cs`, **new** `PlaybackBindBeforeSendTests.cs` | residual: `Drain()` still folds the Connect mailbox before the input ring — only matters if the UI thread stalls across a whole round trip |
| **B2** `PublishGate.Decide` + one-slot held announce (logged), `PublishKey` latched only on 2xx | `Spotify.Connect.cs`, `SpotifyConnectTests.cs` | the held snapshot is *cleared*, not replayed, when the hello goes (the hello re-captures live state) |
| **B3** announce owed to a new connection id, not a phase transition | `Spotify/Spotify.cs`, `SpotifySessionTests.cs` | |
| **B5** full put-state log line + `connect.owner from → to (cause)` | `Spotify.Connect.cs`, `Playback/Playback.Host.cs` | `owner=`/`claim=` on the put-state line are approximations (`us`/`-`): `Snapshot` does not carry `Owner`/`ClaimPhase` — add a field in `Playback.Wire.cs` for the real thing |

### Not started
- **B4** decouple AP and dealer drops (`ApDropped`/`DealerDropped`, independent phases + epochs).
- **B6** AP keepalive state machine ported from librespot (60 s pong delay, PongAck watchdog) — the one *hypothesis* for the resets; land after B4/B5 so the log can show whether the reset rate drops.
- **RC-5** queue panel coherence guard; **RC-7** `SeekAsync` post-seek spin at EOF (engine).
- Issues: none filed. Every fix needs `Fixes #n` + a CHANGELOG ` (#n)`; `gh` calls need the user's approval.

### Live verification still owed (side-folder publish, never the user's instance)
Crossfade 0 + empty queue tail ⇒ `[gapless] ended` then advance · mirror a phone, pause it > one track length, press
play ⇒ sane `fromMs`, a prepared next, header and rows agree · forced dealer drop ⇒ `put-state … active=True` re-announce
and a `put-state held` line during the gap · `connect.owner` lines on a transfer.

---

## 2. Podcast rework

- Prototype approved ("i love the design"). Build plan written: `podcast-show-rework-implementation.md`.
- Shape of it: **`ShowVisit` → New / Returning / CaughtUp** decides the reader's head and the rail's primary; sticky
  word rail; Zune reader rows; new **`episode:` route** with `about · chapters · transcript · comments`; one
  herodotus `ListCurrentStates` hydrate + local mirror + mark played; six new `Detail.FrameSlots`; `Controls.Words`
  promoted out of `User.UI.cs`; chapters as a side table, transcript/comments as `Lyrics.Store`-style stores.
- **Data answer given to the user:** every READ has a known endpoint (the WinUI app at `0d0429a0` ran them all);
  pathfinder hashes are from June and may have rotated — plan §6.0 adds a Diagnostics "Podcast wire check" the **user**
  runs (never an agent-shell `--spotify-*` probe). WRITES with no captured endpoint: rate, comment/reply/react,
  new-episode notifications ⇒ drawn, disabled with a reason, wave P10 gated on a capture. Your-Episodes is probably a
  plain collection write (`ListenLaterSet = "listenlater"` is already parked in `Spotify.Api.Library.cs`).
- Watch-out recorded in the plan: 0.3's `herodotus.proto` has `ResumePoint { int64 position = 2 }`, the WinUI proto had
  `uint32 position_seconds = 1` — verify the unit against `ResumeMs` before mapping.
- Nothing of the podcast plan is implemented. Next: wave P1 (CORE columns, rules, decode, `Controls.Words`, fake seed,
  the wire-check action) once §1's tree is green.

---

## 3. The corrupt store (found at 20:00 from the user's screenshots)

- `%LOCALAPPDATA%\Wavee\library.db` is malformed (`SQLite Error 11`). **Every launch on 09-18 (13 of 13) ran
  memory-only**; the file on disk has not been written since 09-16 22:44. The existing unreadable-file recovery never
  ran because the exception is thrown one line earlier, at `PRAGMA journal_mode=WAL` (`Entities/Store.cs` `Open`).
- User's decision: **clear and rebuild** — it is a cache.
- Written (UNBUILT): `Store.Open` catches `SQLITE_CORRUPT`/`SQLITE_NOTADB` at the open/pragma stage → dispose,
  `ClearAllPools`, delete `.db/-wal/-shm`, log `store.dropped why=unreadable:<code>`, reopen (rethrow ⇒ memory-only
  only when the file is also undeletable). Pure `Store.IsUnreadableFile(code)`. Tests in `StoreTests.cs`
  (garbage file ⇒ store opens and persists; only 11/26 delete — never BUSY/LOCKED).
- Proposed, awaiting the user's yes: (a) the same recovery **mid-session** (a read/write fault with code 11 → close,
  delete, reopen once, one-shot guard); (b) log how many pending `intent` rows (unsynced library writes) were lost;
  (c) root cause — two crash reports that day (13:00, 14:21); check whether `_old`/0.2.x and 0.3 share the same
  `library.db` path.
- Immediate manual remedy: quit Wavee, delete `library.db`, `library.db-wal`, `library.db-shm`.

## 3b. The `/collection/v2/delta` storm (found 21:00 in the user's Fiddler capture — ~940 calls, all 200)

- **Cause (read in source):** `Spotify.Library.ApplyCollectionDelta` discards a delta on a baseline or reconcile
  mismatch and calls `Entities.RefreshEdge` — but never erased the ledger entry, so `Fetch.FillRevisions` handed the
  re-ask the SAME token + count, the provider took the delta path again, failed the same check, re-asked… forever.
  The doc comment promised "with no ledger entry surviving, that next ask is a FULL walk"; the code never did it.
- **A guaranteed trigger:** `collection` is ONE wire set for two relations (liked tracks + saved albums); `added`/
  `removed` were counted over the raw answer, so any delta carrying the *other* relation's items could never reconcile.
- **Invisible because** the api layer logged failures only; a loop of 200s left no trace. Unknown how long it ran.
- **Written (UNBUILT):** `ForfeitLedger` (erase the entry, one `library` Warn line, then refresh ⇒ full walk);
  adds/removes counted over resolved items only. `Spotify/Spotify.Library.cs`.
- **Wire log (UNBUILT):** new `Platform/Platform.Wire.cs` — a `DelegatingHandler` on all six app `HttpClient`s
  (`api`, `session`, `cdn`, `lyrics`, `update`, `github`): one always-on `wire.call client= VERB host/path status= ms=
  sent= recv= [range=]` line per request (never the query string), plus `wire.storm <endpoint>` (Warn) at 40+ calls to
  one id-folded endpoint inside 60 s, repeated every further 40. Storm counting is off for the audio cdn only. Pure
  `WireRules` + `WireRulesTests.cs`. **Extended (also UNBUILT):** cover art via a new engine seam
  `AppOptions.ImageHttpHandler` + public `DefaultImageFetcher.CreateClient(wrap)` (client `images`); AP packets and
  dealer websocket text frames as `wire.send channel=ap cmd=0x.. | channel=dealer type=..` (same storm rule).
  **Still not covered:** the engine's `ToastImageCache` client, and anything inside the fenced PlayPlay tree (its
  licence POSTs are only covered if they go through `Spotify.Api`'s client — unverified, the folder is off-limits).
- Follow-up: a reducer-level test for "a discarded delta's next ask is a full walk" needs a fake provider seam.

## 4. Open observations (no fix yet)

- **Slow track lists**: 3.5 s nav → rows on a cold playlist (`nav.route` → `frame.slow sinceNavMs=3494`). With no
  store every visit after a restart is cold. **0.3 logs nothing on the fetch path** — no `fetch`/`hydration`/
  `catalog.demand` lines exist in this build, so the 3.5 s cannot be attributed. Add one always-on line per batch
  answer (transport, op, rows, ms, status) before tuning anything.
- **Glitched cover art** (top strip decoded, garbage bands, grey below): image cache writes are atomic (tmp + move), so
  not a torn file. Best guess is a truncated download cached as complete (no length check) or a GPU upload glitch;
  there is no image logging to confirm. Unresolved.
- `governor.trim level=Moderate … freed=0B` every 30 s at machineLoad 0.88–0.92, private ~530–830 MB — it fires
  constantly and frees nothing.
- `W`-level noise: `[engine] [wake]/[repaint]/[repaint-causes]` and `[mem] mem.sample` are ~99 % of all warnings and
  bury the real ones.

## 5. Order of work next session

1. Build Debug + Release, run `Wavee.Tests` and the engine gates; fix what the three unbuilt agent waves break.
2. Decide the store follow-ups (a)(b)(c); have the user delete the DB or ship the fix.
3. Live-verify §1; then B4, then B6.
4. Add the fetch-path log line; re-measure the playlist load.
5. File issues (with approval), CHANGELOG bullets, commit.
6. Podcast wave P1.
