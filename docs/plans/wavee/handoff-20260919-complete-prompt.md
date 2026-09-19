# Handoff prompt — Wavee 0.3, everything from the 2026-09-18/19 session

Paste everything below the line into a fresh session.

---

You are picking up work on **Wavee**, a Spotify desktop client for Windows on the FluentGpu engine. Read this whole
prompt, then read the four plan documents it names before touching anything.

## 0. Where the work is — read this first

- **The app the owner actually runs is the Release arm64 publish of the worktree `C:\WAVEE\wavee-0.3`** (branch
  `feat/0.3-structure` @ `02f22cce` + a large UNCOMMITTED tree), engine pinned by `EngineRoot.local.props` to
  `C:\WAVEE\fluent-gpu-pin`. It is **not** `C:\wavee\WaveeMusic` main (0.2.10). Proof is in every log's
  `[app] startup` line: `iconFont=C:\WAVEE\wavee-0.3\…\publish\…`. Code lives in
  `src/apps/Wavee/{Playback,Spotify,Entities,Shell,Platform,Screens}`; `src/apps/_old` is the retired 0.2.x tree.
- The WinUI-era app is reachable only through history: `git -C C:/wavee/WaveeMusic show 0d0429a0:<path>`.
- Logs: `%LOCALAPPDATA%\Wavee\logs\wavee-YYYYMMDD.log` (`t=` is epoch ms). Cache: `%LOCALAPPDATA%\Wavee\library.db`.
- House rules (`CLAUDE.md`, both repos): no env-var switches; no legacy paths (replace outright); no source-text
  tests — decisions go in pure engine-free classes with unit tests; derived facts live on the model; no page-side fetch
  windows; props freeze at mount; every fix references an issue (`Fixes #n`, CHANGELOG ` (#n)`); plans carry real code;
  implementation = Sonnet/Opus subagents on DISJOINT files that never build/test/touch git — the orchestrator does one
  Debug build + one Release build + one test run per wave. Any `gh` call that modifies, any push, any auth/public
  action: ask first. **Never run `--spotify-*` CLI probes from an agent shell** (a rejected stored login wipes
  `store.json`). Never read `**/.native/**`, `**/Wavee.PlayPlay/**`, `private-runtimes/**`, `*playplay*` docs/tools.
  The owner works in the same tree and runs the app while you work: foreign changes are theirs; never stop their
  instance; a second Wavee.exe hands off to the running one and exits.

## 1. STATE OF THE TREE — nothing written in this session has been built by the orchestrator

Everything in §3 was written by reading only. The orchestrator's one build attempt was stopped by the owner. The owner
HAS been publishing and running the tree themselves (the `wire.call` log and the store recovery demonstrably work in
their 20:20+ builds), so it evidently compiles — but **no test run has happened and the engine gates have not run.**
**First action: wave D0** — `dotnet build src/apps/Wavee.Tests/Wavee.Tests.csproj -c Debug`, the same `-c Release`,
`dotnet test` on it, then in `C:\WAVEE\fluent-gpu-pin`: `dotnet build src/FluentGpu.slnx` Debug + Release and the engine
tests with `--blame-hang-timeout`. Pipes mask exit codes; the owner's running instance can lock output folders. Ask the
owner before building if they are mid-session.

## 2. The documents (all in `C:\WAVEE\wavee-0.3\docs\plans\wavee\`)

| File | What it is |
|---|---|
| `playback-end-and-connect-fix-plan-20260918.md` | the approved plan: playback/Connect diagnosis + fix waves A/B + the podcast prototype brief |
| `podcast-show-episode-mica.html` | the approved podcast prototype — https://claude.ai/artifact/92Ly7eaziCbWB4r1x4sgYU ("i love the design") |
| `podcast-show-rework-implementation.md` | the full podcast build plan, waves P1–P10 |
| `cache-integrity-and-playlist-diff-implementation.md` | the database / playlist-`/diff` / request-shape plan, waves D0–D5, incl. §3.7–3.10: four decoded Fiddler captures |
| `handoff-20260918-playback-connect-podcast-store.md` | the evening's running status doc |
| this file | the consolidated handoff |

## 3. What was found and what was written (UNVERIFIED code — see §1)

### 3.1 Playback stuck at 4:04, never advancing (proven from logs + source)
Chain: Wavee mirrored a remote Connect device; "Closer" sat as the mirrored row for 2 h 23 min → `DoTick` folded the
extrapolated position every second with no owner test and `MirrorRemote` stamped the mirror with the LOCAL clock →
`Position()` clamps to the duration, so the stale mirror ratcheted to exactly 244 960 ms → play pressed ⇒
`LoadFromMs` = the full duration ⇒ `audio.seek.short … eof=1` → nothing prepared because adopting a cluster never sets
`Context`/`Cursor` (`Queue.DecideSeed` refuses to re-seed a non-`Unknown` queue) so `NaturalNext` returns nothing, and
the endgame nudge was a once-per-load latch (same cause for "Playing from Liked Songs" over 49 daylist rows) →
**`PlaybackState.Ended` was unreachable on a real WASAPI device**: a drained mixer kept rendering silence into the sink,
so `WritableFrames >= CapacityFrames` never held (53/53 transitions that day were crossfades, 0 `Ended`; it had been
fixed only for `--fake`).
Written: **A1** engine `DrainVerdict.Decide` + `RenderBlock` short-circuit gated to `_transportPhase == 0`
(`fluent-gpu-pin/…/Audio/PcmAudioPlayer.cs`, new `DrainVerdict.cs`, new `DrainVerdictTests.cs` incl. an RT-feed +
buffered-sink test), the `--fake` workaround deleted from `Playback.Audio.cs` · **A5** `Playback/Playback.Endgame.cs`
`EndgamePlan.Decide` (re-asks every 3 s, `[gapless] ask n=`; orchestrator fix: the arm line is still owed on the commit
tick — an 8.5 s fade arms and commits on the same tick) · **A2** `MirrorSnapshot.Project` + `DoTick` folds only when
`Owner.Us` · **A3** `ResumeStart.For` (≤ 1.5 s from the end ⇒ next row or 0) + `SeekTarget.Clamp` (750 ms tail guard) ·
**A4** takeover adopts context and re-seeds queue/cursor (`DecideSeed(takeover)`, `Effects.TakeoverSeed`; orchestrator
wired the two hooks in `Playback.Host.cs`: `RemoteState` carries `d.ContextUri`, `Execute()` runs
`SeedQueueFromCluster(takeover: true)` before Stop/Load). New tests: `MirrorSnapshotTests`, `ResumeStartTests`,
`SeekTargetTests`, `PlaybackEndgamePlanTests`, additions to `PlaybackStepTests`, `QueueSeedTests`, `PlaybackAudioTests`.
Not done: RC-5 queue-panel coherence guard; RC-7 engine `SeekAsync` post-seek spin at EOF.

### 3.2 Connect "not showing as playing elsewhere"
Found: ~29 AP resets/day (`SocketException 10054`); ANY drop (AP or dealer) does `CloseAll`, wipes the connection id and
forces a `NewDevice` re-announce; announces made while the id is empty were silently dropped; `PutSent` was posted only
after a 2xx so a failed PUT could never settle the claim; a new connection id while Online was never announced; `Flush`
unserialised on 4 workers; no ownership logging in the file. Hypothesis (unproven): the resets are provoked by the
keepalive (immediate Pong, PongAck ignored; librespot waits 60 s and watches the ack).
Written: **B1** bind before send + serialised/re-armed `Flush` · **B2** `PublishGate.Decide` + one-slot held announce +
`PublishKey` latched only on 2xx · **B3** announce owed to a new connection id (`Spotify.cs`) · **B5** full put-state
log line + `connect.owner from → to (cause)` (`owner=`/`claim=` on the put-state line are approximations — `Snapshot`
lacks `Owner`/`ClaimPhase`; add a field in `Playback.Wire.cs`). Residual: `Drain()` folds the Connect mailbox before
the input ring. **Not started: B4** (split `ApDropped`/`DealerDropped`, independent phases/epochs) and **B6** (AP
keepalive state machine ported from librespot) — B6 lands after B4/B5 so the log can show whether resets drop.
Live verification owed (side-folder publish only): crossfade 0 + empty queue tail ⇒ `[gapless] ended` then advance;
mirror a phone, pause it longer than a track, press play ⇒ sane `fromMs`, a prepared next, header and rows agree;
forced dealer drop ⇒ `put-state held` then an active re-announce; `connect.owner` lines on a transfer.

### 3.3 The corrupt cache (root cause proven at byte level)
`library.db` passes `integrity_check` alone (4 726 pages, change counter 56, cookie 30); its `-wal` holds page 1 of a
DIFFERENT database (7 701 pages, counter 7, cookie 29) ⇒ `SQLITE_CORRUPT` at the first pragma ⇒ **13 launches on 09-18
ran memory-only with no log line**. Mechanisms in source: `Store.Delete` removes `.db/-wal/-shm` one by one, swallows
failures, returns `!File.Exists(path)` (main file only); 0.2.x, 0.3 Debug and 0.3 Release all share
`%LOCALAPPDATA%\Wavee\library.db` with different schemas; `Entities.Boot` (opens/deletes the store) runs at `App.cs:69`
BEFORE the single-instance gate (`Shell.Host.cs:149-163`); 8 crash reports on 09-17/18. Exact interleaving unrecoverable.
Owner's decision: **clear and rebuild — it is a cache.** Written: `Store.Open` recovers at the open/pragma stage
(`IsUnreadableFile` 11/26 → dispose, clear pools, delete, `store.dropped`, reopen) + `StoreTests`. Confirmed working in
the owner's build (file recreated 20:20). The rest is wave D1.

### 3.4 `/collection/v2/delta` storm (~940 × 200, found in Fiddler)
`ApplyCollectionDelta` discarded a delta and called `RefreshEdge` without erasing the ledger ⇒ same token ⇒ same delta
⇒ forever. Guaranteed trigger: `collection` is one wire set for liked tracks + saved albums and adds/removes were
counted over the raw answer. Written: `ForfeitLedger` + relation-scoped counting (`Spotify/Spotify.Library.cs`).
Confirmed fixed (4 calls/launch). Owed: a fake-provider test that a discarded delta's next ask is a full walk.

### 3.5 Every outbound request is now logged
New `Platform/Platform.Wire.cs`: a `DelegatingHandler` on all six app `HttpClient`s (`api`, `session`, `cdn`, `lyrics`,
`update`, `github`) + engine seam `AppOptions.ImageHttpHandler` / public `DefaultImageFetcher.CreateClient(wrap)`
(`images`) + AP packets and dealer frames (`wire.send channel=ap cmd=0x.. | channel=dealer type=..`). Lines:
`wire.call client= VERB host/path status= ms= sent= recv= [range=]` (never the query string) and `wire.storm <endpoint>`
(Warn, 40+/60 s to one id-folded endpoint; off for cdn/images). Pure `WireRules` + `WireRulesTests`. Not covered: the
engine's `ToastImageCache`; PlayPlay licence POSTs only if they use the api client (fenced — unverified).

### 3.6 What the wire log exposed (all in the D plan)
- **Playlists never `/diff` across launches**: membership, rootlist and the playlist `Revision` are not persisted by
  design (`Store.RegisterLibraryEdges` doc, `PlaylistShape` doc). Owner: "of course playlists should be saved too and
  diffed only on changed data."
- **~30 full playlist reads at every boot**: the sidebar asks the MEMBERSHIP edge for a COUNT
  (`Sidebar.cs:4464-4467` `ShouldEnsureCount`; pin band `Sidebar.Host.cs:3086-3097`); the only route is the full read.
- **One-uri POSTs**: 122 `extended-metadata` POSTs in 62 s, median body ~100 B — `Fetch.Plan/PlanEdge/Continue` end in
  an inline `Pump()`; the only coalescing is accidental back-pressure at `MaxInFlight = 4`. Best lead (unproven) for the
  3.5 s cold playlist load. (The official client is chatty too: 171 in 38 s — "fewer than the official client" is the
  honest yardstick.)
- **Boot 401s**: nothing gates the planner on the session — no token, unresolved `spclient.wg` host.
- **Per-playlist dealer pushes are dropped** (`LibraryPushRules.Classify` only knows `/rootlist`).
- `Settings ▸ Storage ▸ Clear metadata` is dead (`ClearMetadataCache` never assigned).

## 4. The D plan in one paragraph (details + code in `cache-integrity-and-playlist-diff-implementation.md`)
**D1** schema-named file `library.<fingerprint>.db`, store opened AFTER the single-instance gate, rename-first
all-or-nothing `Delete`, a reaper for old generations/legacy/`.dead-*`, mid-session recovery (`StoreHealth.OnFault`,
once per process), always-on `store.open` line, lost-intent count, `Store.DropCatalog` wired to "Clear metadata".
**D2** `list_head`/`list_item` tables (rows + revision + total in ONE transaction; text columns, `added_by` as text;
strings re-interned by the applier — the Pins precedent), `ListWrite.MayPersist` refuses partial windows / pending rows /
resync answers / bad revisions, the edge door gets a disk leg, rootlist warmed at boot, sidebar count comes from the
persisted row. **D3** pure `PlaylistOps.TryApply`, `ListFreshness.Decide` (first open per session revalidates; 5-min
window; ≤ 1500 ms blocking revalidate), `ListPush.Decide` (in-place only when resident and `stored == parent_revision`;
head-only pushes never store a revision), `list.replay ops= kinds= verdict=` log line. **D4** `Fetch.Drain()` once per
UI tick (Playback stays immediate), `Fetch.CanSend` holds — never drops — until Online, bucket key drops priority,
per-row callers become span asks, `fetch.send/answer` lines. **D5** docs + issues.
NOT copied from 0.2.x on purpose: trusting `/diff` contents without checking `changes_require_resync` (0.3's own bug
A1), header/rows/revision in different transactions, ignoring `truncated`, persisting optimistic rows, no account
isolation/schema identity/corrupt-file handling.

## 5. Wire facts proven by four Fiddler captures (official desktop client 1.2.96.518; NO Wavee traffic in any)
Captures: `C:\Users\ChristosKarapasias\Documents\Fiddler2\Captures\{reorders,morediffs,somemore,more}.saz`. The decoders,
decoded trees and ~825 decompressed protobuf bodies are kept PERMANENTLY in **`C:\WAVEE\wavee-captures\playlist4-2026-09\{saz,saz2,saz3,saz4}`** — start
with its `README.md` (which file proves which rule; the two replay chains with real baselines). It is outside every
repo on purpose: the bodies name the owner's account, playlists and tracks, and the raw credentialed sessions were NOT
copied. **Ask the owner which playlists may become `Wavee.Tests/Fixtures/playlist-ops/` before copying anything in.**
1. Clients WRITE keyed ops (`MOV{items, add_after_item|add_first}`, `REM{items, items_as_key}`,
   `ADD{items, add_last | undeclared field 7 = add_after_item}`); the service ECHOES positional ops in dealer pushes and
   in `/diff` answers.
2. A multi-revision gap is ONE flat positional op list applied SEQUENTIALLY; it can mix row ops with
   `UPDATE_LIST_ATTRIBUTES` (rename: `new{name,description}`, `old{name}`, `no_value=[LIST_DESCRIPTION]`). REM carries
   every removed row in order (uri + item_id + added_by + timestamp); MOV carries none; ADD carries full items.
3. **MOV: `to_index` is in pre-removal coordinates — insert at `to > from ? to − length : to`.** Proven for forward
   length 1 (raw dealer bytes: `{0,1,2}` then `{3,1,1}` with keyed requests as ground truth), backward block `{5,8,0}`,
   forward block `{0,3,5}` by arithmetic (5 cannot be a final index in 6 rows). Rootlist 135→137
   (`ADD{0,[start-group…, end-group…]}` + `MOV{2,1,1}`) replays to an EXACT match of the real rev-137 read.
4. **Create folder = ONE ADD with TWO items** (start + end marker); move into folder = a MOV between them; rootlist rows
   have no `item_id` (identity = uri).
5. A diff-bearing answer never sets `length` or top-level `attributes`; `from_revision` always equals the requested
   revision; `/diff` answers: 304-empty (no conditional header — keyed off `revision=`), 200 empty diff, 200 full
   `contents` (request named `revision=0,…`, or some editorial lists), 200 with ops. Never `up_to_date`,
   `multiple_heads=true`, or `contents` beside a `diff`.
6. `/changes` answers: `changes_require_resync=true` for every ADD, false for MOV/REM; the client obeys with a full GET.
   Bulk-ADD pushes and editorial daily-refresh pushes are head-only (no ops, no `parent_revision`).
7. `SelectedListContent` field 23 = 1 user-owned, 0 editorial/algorithmic (hundreds of answers, no exception).
8. The official client `/diff`s its whole rootlist at startup (91 in 38 s, mostly 304s).
Proto gaps to declare in `Protos/playlist4_external.proto` (never guess; unknown fields must round-trip): `Add` 7 (and
6 by symmetry, unobserved), `SelectedListContent` 23, `ListAttributes` 16, `MetaItem` 7/9, `ItemAttributes` 17;
`Capabilities` 7–23 and `LensState` 2/3 noted. Still UNOBSERVED: `UPDATE_ITEM_ATTRIBUTES`, folder rename/delete, a LIVE
rootlist dealer push (topic, shape, delivered twice?) — so a rootlist push only ever `MarkDirty`s —, 509, `up_to_date`,
`multiple_heads`. Those are rule-derived + full-read-on-doubt.

## 6. Podcasts
Approved prototype + plan (`podcast-show-rework-implementation.md`). Shape: keep the two panes; `ShowVisit.Of` → New /
Returning / CaughtUp drives the reader's head and the rail primary (Follow → Resume · 23 min left → Play latest); sticky
word rails; Zune reader rows (month groups, big numerals, hover actions, now-playing); new `episode:` route (rail +
tabs `about · chapters · transcript · comments`, doors, more-from-show, recommendations); ONE herodotus
`ListCurrentStates` hydrate + local mirror + mark played (today `EpisodeFields.Progress` is written by NOTHING in this
build); six new `Detail.FrameSlots`; `Controls.Words` promoted out of `User.UI.cs`; chapters as a side table,
transcript/comments as `Lyrics.Store`-style stores; per-show filter/sort in one capped setting. **Owner direction:
design EVERYTHING** (ratings, topics, similar shows, chapters, transcript, comments…) — the WinUI app ran every READ
path; hashes are from June and may have rotated, so plan §6.0 adds a Diagnostics "Podcast wire check" the OWNER runs.
WRITES with no captured endpoint (rate, comment/reply/react, new-episode notifications) are drawn, disabled with a
reason, and gated on a capture (wave P10); downloads are out of scope. Watch-out: 0.3's `herodotus.proto`
`ResumePoint { int64 position = 2 }` vs WinUI's `uint32 position_seconds = 1` — verify the unit against `ResumeMs`.
Nothing implemented. Starts after D0 is green.

## 7. Open observations with no fix yet
Glitched cover art (top strip decoded, garbage bands, grey below): disk writes are atomic (tmp + move), so not a torn
file — best guess a truncated download cached as complete; no image logging existed to confirm (there is `wire.call
client=images` now). · `governor.trim level=Moderate … freed=0B` every 30 s at machineLoad ~0.9. · `[engine]
[wake]/[repaint]/[repaint-causes]` and `[mem] mem.sample` are logged at W and are ~99 % of all warnings. · 3.5 s cold
playlist load, unattributed until D4's `fetch.send/answer` lines exist.

## 8. Issues and git
Nothing is committed; no issue is filed. Lists are in the two plan docs (§9 of the D plan, §13 of the podcast plan) plus:
playback never ends on a real device; stale-mirror resume at EOF; takeover without context/cursor; Connect announce/
claim defects; AP/dealer coupling; AP keepalive. Every fix needs its issue, CHANGELOG bullet and `Fixes #n`. Ask before
any `gh` call.

## 9. Suggested order
D0 (green tree) → live-verify §3.1/§3.2 → **D1** → **D2** → **D3** → **D4** → B4 → B6 → podcast **P1…** → D5/issues.
Ask the owner to confirm the order and whether the four captures' bodies may enter the repo as fixtures.
