# Handoff — 2026-09-19 session 2: waves D1–D4, B4, B6, podcast P1–P2 written; two fresh official-client captures investigated

> Follow-up: the podcast/episode implementation and capture-parity fixes are now recorded in
> [podcast-20260919-implementation-status.md](podcast-20260919-implementation-status.md).
> That report supersedes this session-2 snapshot for those items and records the integrated validation results.


Everything from this session, with every document referenced. Nothing written this session has been BUILT or TESTED by
the orchestrator — the owner said to skip builds ("its already working") and later "stop building and testing at every
single checkpoint". The owner builds and publishes the worktree themselves.

---

## 0. Where the work is

| What | Where |
|---|---|
| Worktree (the app the owner runs, Release arm64 publish) | `C:\WAVEE\wavee-0.3`, branch `feat/0.3-structure` |
| Last commit (checkpoint, start of this session) | `41782408` — "0.3: library rework, video, the 09-18 playback/Connect/store fixes, the wire log, and the D and podcast plans" (111 files, committed at the owner's request WITHOUT issues) |
| Engine pin | `C:\WAVEE\fluent-gpu-pin`, branch `feat/0.3-engine-live` @ `feb0fb0a4` (DrainVerdict, image-client seam, library-rework engine patches) — clean |
| Uncommitted since `41782408` | 117 paths (75 modified, 42 new) — every wave below |
| Entry handoff this session started from | `docs/plans/wavee/handoff-20260919-complete-prompt.md` |
| **As-built notes for every wave (the detailed record)** | **`docs/plans/wavee/as-built-20260919.md`** — linked from each plan's "As built" section |
| Capture investigation | `C:\WAVEE\wavee-captures\fresh-client-2026-09-19\` (outside every repo) — §5 |
| Owner-approved order (start of session) | D1 → D2 (+ podcast P1 in parallel) → D3 → D4 → B4 → B6 → P2… → D5 |
| Playlist4 fixtures policy (owner) | scrubbed re-encodes only (done: `src/apps/Wavee.Tests/Fixtures/playlist-ops/`) |

---

## 1. State right now

- **Not built, not tested.** 117 changed paths written by agents reading source only. An Opus verifier checked the
  cross-agent seams of D1/D2-sidebar/P1 (one compile error + one JSON-exception gap found and fixed); the later waves
  (D2 lists, D3, D4, B4, B6, P2) were not seam-verified. The first build will surface compile errors — see §6.1 for
  the lines the agents themselves flagged as build risks.
- **Agents that were running during the session** (all finished; nothing is running now):
  1. ~~P0 herodotus fix~~ — DONE (as-built "Herodotus resume-point fix"): Duration write/read, the oneof arms,
     REPEATED revisions (a second bug), limit 1000, mark-played choice; the write for 820 912 ms is byte-identical to the
     official request. Markers 3/4 are provisional. See §5.2 item 1.
  2. ~~P3 show reader~~ — DONE (as-built "P3 (show reader)"; §3 table). On LIVE data its episode list still depends on
     §5.2-5 (the ShowV4 field-70 route never answers) — it shows episodes in `--fake` only until that fix lands.
  3. ~~Wavee-vs-official comparison~~ — DONE, saved as `findings-wavee-vs-official.md` (§5.2 items 6–7, §5.3 item 16).
- **Stopped by the owner's instruction (made NO edits):** three fix agents — Connect acks/ghost/ids, request
  envelope + list headers, show episode list via `playlist/v2/show`. Their scopes are in §6.2 (not done).
- **No issues filed, no CHANGELOG bullets, nothing committed since `41782408`.** Every fix needs its issue
  (`Fixes #n`, CHANGELOG ` (#n)`); every `gh` call needs the owner's approval.

---

## 2. Every document

### 2.1 Plans (worktree `docs/plans/wavee/`)

| File | What |
|---|---|
| `playback-end-and-connect-fix-plan-20260918.md` | playback/Connect diagnosis + waves A (A1–A5) / B (B1–B6); "As built" → as-built B4/B6 |
| `cache-integrity-and-playlist-diff-implementation.md` | the D plan: D0–D5, §3.7–3.10 wire facts from the four earlier captures; §10 "As built" → as-built D-sections |
| `podcast-show-rework-implementation.md` | podcast plan, waves P1–P10; §14 "As built" → as-built P-sections |
| `podcast-show-episode-mica.html` | approved podcast prototype (https://claude.ai/artifact/92Ly7eaziCbWB4r1x4sgYU) |
| `library-rework-implementation.md`, `library-rework-mica.html`, `library-stabilization-plan.md` | library rework (landed in `41782408`) |
| `handoff-20260918-playback-connect-podcast-store.md` | 09-18 evening status (superseded) |
| `handoff-20260919-complete-prompt.md` | the consolidated handoff this session started from |
| **`as-built-20260919.md`** | **per-wave as-built notes, deviations, OPEN items (this session)** |
| this file | the session-2 handoff |

### 2.2 Capture investigation (outside every repo: `C:\WAVEE\wavee-captures\fresh-client-2026-09-19\`)

| File | Content |
|---|---|
| `vc1\` | `verycomplex.saz` — official 1.2.96.518 COLD start, 837 sessions, 13:46:59–13:48:09; **also contains Wavee's own traffic** (Wavee was the Connect device being remote-controlled) |
| `vc2\` | `verycomplex2.saz` — WARM relaunch 86 s later, 407 sessions, official only |
| each `vcN\`: `index.tsv`, `pathfinder.tsv`, `extmeta.tsv`, `withheld.tsv`, `headers\` (redacted), `bodies\` (decompressed) | the dump (`dump_all.py`); `extracted\` holds live credentials — delete when done |
| `findings-podcast-pathfinder.md` | podcast/episode pathfinder ops, comments (read + WRITE), ratings |
| `findings-podcast-wire.md` | non-pathfinder podcast traffic: herodotus, show lists, transcripts, chapters, video, autopodcast |
| `findings-pathfinder.md` | every pathfinder op + the full hash audit of Wavee; Appendix A = the official client's own 152-op persisted-query table |
| `findings-session-connect.md` | cold-start timeline, auth shape, Connect (two-party), telemetry (gabo), config, header parity |
| `findings-lists-metadata.md` | playlists/rootlist/`/diff`, permission, popcount, collection, extended-metadata, images, audio (corrected version: Wavee's traffic separated) |
| `findings-wavee-vs-official.md` | Wavee's own 60 sessions vs the official client, correlated with Wavee's log (session `bea50b48`, pid 22512): per-session table, put-state field-by-field, herodotus, autopodcast, the two-party Connect trace, 17 prioritised actions |
| `work-*\` | each agent's scripts and decoded dumps (e.g. `work-pathfinder\bundle_ops_1.2.96.518.tsv`, `audit_table.tsv`) |

Earlier captures (unchanged): `C:\WAVEE\wavee-captures\playlist4-2026-09\README.md` (four 09-18/19 playlist captures).

### 2.3 Memory (auto-memory, `…\.claude-work\projects\C--wavee-waveemusic\memory\`) — updated this session

`wavee-0.3-worktree-state.md` (HEAD, pin, order, entry doc) · `wavee-0.3-model-split-and-minimal-gates.md` (NO
builds/tests between waves or at checkpoints) · `fresh-client-captures-2026-09-19.md` (the capture folder + gotchas) ·
`synthesize-before-fanning-out.md` (after research: synthesis + proposed order, then stop and let the owner choose).

---

## 3. What was built this session (details, deviations and OPEN items per wave: `as-built-20260919.md`)

| Wave | Result (one line) | as-built section |
|---|---|---|
| **D1** cache file | schema-named `library.<fp>.db`; single-instance gate hoisted into `App.Main` before any store opens (`Shell.AcquireInstance`); rename-first all-or-nothing `Delete`; `StoreFiles.Reap` (legacy/old generations/`.dead-*`); one `Recreate` path; mid-session `Rebuild` (once per store session); `store.open`/`store.recovered`/`store.reap`/`store.cleared` lines; "Clear metadata" wired to `Store.DropCatalog` (keeps library edges + intent journal — deliberate deviation) | D1-S1, D1-S2 |
| **D2** sidebar | count is a row fact; `ShouldEnsureCount` deleted; mosaic asks at Prefetch; pin fallback | D2-U |
| **D2** lists on disk | `list_head`/`list_item`; `ListWrite.MayPersist`; save from `WriteBehind` in one transaction; disk-first edge door (`AskDisk`/`AfterDisk`); rootlist warmed at boot; `TrackCount==0` mask removed | D2-L |
| **D3** pure | `Spotify.Playlist.Ops.cs`: `PlaylistOps.TryApply`, `DecodeDiff`/`DecodePush`, `ListFreshness`, `ListPush`; proto gaps declared; 29 scrubbed fixtures | D3-pure |
| **D3** replay arm | `DiffVerdict` deleted; `ListReplay.Decide`; `Store.SnapshotList`/`StageList`; `Fetch.FillBaselines`; `list.replay` line | D3-L2 |
| **D3** dealer | per-playlist pushes (`hm://playlist/v2/playlist/{id}`), `ListStamps`, apply-in-place, reconnect revalidate, `Fetch.CanSend` wired | D3-L3 |
| **D3** open | `ListOpenPolicy`/`ListOpen` (moved to `Entities/Playlist.Open.cs`), reveal hold ≤ 1.5 s, `Fetch.ListSettled` hook | D3-U1 |
| **D3** fix | optimistic row edits forget the held revision (`Entities.ForgetListRevision`); guarded rootlist head adoption | D3 fix |
| **D4** planner | `Fetch.Drain()` once per UI tick, `CanSend` holds until Online, bucket key without priority, `fetch.send`/`fetch.answer` | D4-F1 |
| **D4** callers | per-row asks → span asks (pins, restored episodes, Connect `EnsureRow`) | D4-F2 |
| **B4** | AP and dealer drops decoupled (own phases/epochs/backoff); silent thread deaths on HttpClient timeouts fixed | B4 |
| **B6** | librespot keepalive (`ApKeepAlive`, pong held 60 s, ack watchdog 20 s); reconnect keeps known tier; connection-id repeat compares bytes | B6 |
| **P1** | Show/Episode columns + flags; decode; fake seed (New/Returning/CaughtUp); `Show.Rules.cs`; `Controls.Words` + podcast controls + all loc keys; Diagnostics "Podcast wire check" | P1-* |
| **P2** | six `Detail.FrameSlots`; `episode:` route + placeholder page + actions; progress hydrate + local mirror + `MarkEpisode` + per-group authority | P2-M, P2-S, P2-T |
| **P0 herodotus** | resume point = `google.protobuf.Duration` in a `CurrentStateValue` oneof; repeated revisions (newest wins); limit 1000; mark played = Duration of the full length | Herodotus resume-point fix |
| **P3** | show page rebuilt as the prototype's reader: sticky word rail + find, visit head (New doors / Returning continue + up next + new since / CaughtUp / Unavailable), month groups, `Episode.ReaderRow` (numerals, states, hover cluster, now-playing), rail slots (badges, publisher, rating, ledger, primary by visit, satellites), `Episode.Menu` + MarkPlayed/MarkUnplayed/GoToShow actions; old card/banner/toolbar deleted | P3 (show reader) |

### 3.1 Orchestrator's own fixes this session
Headless `--store` registers shapes first · show route asks `ShowFields.Facts` · last count-driven sidebar read removed ·
verifier fixes (CS0165 in the wire check, JSON-exception guard, count-fallback repaint fold, `FileGlob` deleted) ·
`Fetch.cs` baseline hooks · obsolete `DiffVerdict` tests deleted · dealer payload forwarded (`Spotify.Connect.cs`) ·
`Store.PrepareUpsert` NULL-authority merge (sqlite `max()` returns NULL when any arg is NULL — affected every shape) ·
LoadHost's redundant episode resolve removed · `WelcomeMarket` (late country packet no longer switches the scope) ·
**six rotated pathfinder hashes swapped** (§5.3) · wire check: `token`/`pageToken`/`reactionUnicode` sent as JSON null
when absent; first comment uri read from `items[0]`'s own top-level `uri`.

---

## 4. Owner decisions taken this session
- Skip the D0 build; no builds/tests between waves (memory).
- Commit the whole tree before D1 (done: `41782408` + pin `feb0fb0a4`).
- Order: handoff order with P1 alongside D2.
- Fixtures: scrubbed re-encodes only.
- Unknown proto fields: no extra capture needed for the FIELDS; a capture would only help for unseen OP shapes
  (live rootlist dealer push, folder rename/delete) — optional.
- Stop the three fix agents launched after the capture investigation; the owner picks what is fixed and in what order.

---

## 5. The capture investigation — method and results

### 5.1 Method
Both `.saz` unzipped to `vcN\extracted\`, dumped by `dump_all.py` (auth bodies + PlayPlay withheld and never read).
A dump bug truncated every multi-frame zstd response at 65 536 B (Spotify flushes zstd in 64 KiB frames; python
`decompress()` stops at frame 1) — fixed (`stream_reader(read_across_frames=True)`) and both captures RE-DUMPED; any
analysis made before 14:18 on a 65 536-byte body was redone. Five parallel Opus agents analysed disjoint areas; their
findings are the files in §2.2. **Wavee's own sessions** are identified by `User-Agent: Spotify/129400583` (60 in vc1,
1 in vc2) and `Spotify/129300667` (6 fenced key calls). Capture gaps: no `*.scdn.co` (audio-fa, i.scdn.co) or acast host appears in either capture for either client (Fiddler filter) — audio/image downloads cannot be compared; Wavee's dealer socket predates the capture (its ACKs are known from the log only).

### 5.2 Broken NOW in the owner's running build (P0)
1. **Wavee corrupts the account's podcast progress.** herodotus `ResumePoint` is a `google.protobuf.Duration`
   `{int64 seconds=1; int32 nanos=2}`; Wavee writes `ms × 1000` into field 2 (nanos) ⇒ 998 943 ms stored as 0.999 s,
   pushed to every device; positions ≥ 1000 s are invalid Durations; the read is wrong too. "Completed = no resume
   point" is also wrong: `CurrentStateValue` is a oneof (2 Duration · 3 marker · 4 marker · 12 context). Evidence:
   `findings-podcast-wire.md` SUMMARY 1–2. **Fix WRITTEN** (§1; as-built "Herodotus resume-point fix"). The P2-T note,
   the podcast plan §5.0 row and `wavee-fluentgpu-playback-plan.md` §8.1 "microseconds" claims are marked REFUTED.
   Already-corrupted values on the account stay until each episode is written again.
2. **Connect — Wavee leaves commands un-acked.** The official controller logged `ack_timeout` (~30 s) for 3 of 8
   commands (2 seek_to, 1 resume): its ack is the target's put-state echoing `last_command_message_id`, and Wavee's
   content-key dedup (`Connect.IsRedundant`/`PublishKey`, no command id) swallowed the PUT. `findings-session-connect.md`
   SUMMARY 2.
3. **Connect — quitting leaves a ghost**: no final inactive/paused PUT; the cluster kept Wavee's last state with
   `is_playing=true`; the gabo shutdown flush never POSTed (play registrations lost). SUMMARY 3.
4. **Connect — stale ids after a load**: the first 1–2 PUTs after an inbound play carry the PREVIOUS
   `playback_id`/`session_id`/`session_command_id`; official `session_command_id` = the creating command's `command_id`.
   SUMMARY 4–5.
5. **Show pages get no episode list on live data**: ShowV4 (kind 11) has no field 70 (0/8); the episode list is the
   playlist4 list `GET /playlist/v2/show/{id}` (+ `/diff`, 304s). `findings-podcast-wire.md` SUMMARY 3.
6. **A remote `resume`/`seek_to` on a FAULTED episode is acked and then swallowed** — no put-state, no log line. Remote
   played an acast-hosted episode, Wavee faulted (`fault=Unavailable`) and reported `is_paused=1, resume allowed`; the
   remote's `seek_to 0` ×2 and `resume` got only dealer ACKs: `DoResume` returns on `s.Error != Fault.None`,
   `DoSeek`'s parked branch never announces. The remote stayed stuck (the cluster still showed the fault state 10 s
   later). A SECOND "stuck resume" path, not covered by plan wave A. `findings-wavee-vs-official.md` SUMMARY 2, §4 step 6.
7. **External MP3 length taken from a bogus HEAD `Content-Length: 2`** (acast) ⇒ `audio.open … len=2` ⇒ `mp3 open failed`
   — the trigger of item 6. `Spotify.Audio.ExternalLength` accepts any value > 0. `findings-wavee-vs-official.md`
   SUMMARY 3, §6 #3.

### 5.3 Wrong but not failing yet (P1)
8. **Rotated pathfinder hashes — SWAPPED this session** in `Spotify.Api.cs` `Queries`: home + homeSection
   `76243c78…`, searchPlaylists `d520014e…`, searchUsers `8f358dd8…`, queryNpvArtist `4ac064f5…`, getTrack `1a2f0cce…`.
   The old ones still answered 200 on 09-19 (logs: ~600 calls, 0×400). Verify once live: Home, a Playlists search, a
   Profiles search. `findings-pathfinder.md` §2, §8 A1.
9. **First playlist open after a relaunch still sends a full read** beside D2's `/diff`: `Playlist.Page.cs` asks
   `Ensure(All)`; Daylist/Chart/Tuning are not persisted and `PlaylistRead` is their primary route.
   `findings-lists-metadata.md` §7.2 P1-1.
10. **List route headers missing** on playlist/rootlist reads and `/diff`: sync reason (`CAwQAQ==` full read ·
   `CAEQAQ==` revalidation · `CAU=` rootlist read · `CAw=` rootlist diff · `CAI=` push-triggered), `spotify-apply-lenses`,
   `spotify-applied-lenses: auto: YXV0bw==` (Wavee sends `auto`), `x-accept-list-items`, geoblock, dsa.
   `findings-lists-metadata.md` §2.2, §7.2 P1-2.
11. **Home sends `timeZone: "Etc/UTC"`** (InvariantGlobalization blocks Windows→IANA). `findings-pathfinder.md` §3, A3.
12. **Episode number is field 89 (season 88)**, not 65 (P1 decoded 65 — never present in 1067 episodes); 97 never
    present; video shows are media_type MIXED. `findings-podcast-wire.md` SUMMARY 4.
13. **Connect put-state fidelity**: `play_origin` overwritten with `harmony`; `context_metadata` thin; no track uids;
    47–50 prev rows (official 10 + delimiter); episodes marked `disallow_setting_playback_speed`; device info
    strings; registration is NEW_CONNECTION(9) then AUDIO_DRIVER_INFO_CHANGED(11), never NEW_DEVICE, no capability 22.
    `findings-session-connect.md` SUMMARY 6–7.
14. **Gabo telemetry protocol differences** (sequence numbers per (sequence_id, event), monotonic clock id per launch,
    version string, device model, AudioRouteSegmentEnd shape, per-event rejections ignored). SUMMARY 8.
15. **Wire check bugs** — fixed this session (null tokens; comment uri from `items[0]`).
16. **From the Wavee-vs-official comparison** (`findings-wavee-vs-official.md` §6): autopodcast is seeded with the
    show's own episodes as `application/json` (official: recently-played episode heads, form-urlencoded) and Wavee
    queued the CURRENT episode as its own next autoplay row; `session_id`/`started_playing_at` reset on every remote play
    (official keeps them per context session); `index.track` is the position in Wavee's trimmed 50-row window (official
    `{}`); User-Agent OS field `10.0.26340.0` (official 3 parts; `Identity.OsDescriptor` → `v.ToString(3)`); apresolve
    asks `type=dealer` (official `dealer-g2`); Wavee prefetches TWO neighbours' storage-resolve + head + key at every
    load (6 key exchanges for 2 played episodes); outbound `seek_to`/`resume` lack `value`/`relative`/`resume_origin`;
    PUT message ids 43 and 52 minted but never sent (content-key dedupe); both Connect devices are named "CHRISLAPT";
    identity `129300667` = the same Wavee process on the fenced key path only. The server refused nothing (60/60 × 200).

### 5.4 Smaller (P2/P3)
Cover size: rootlist decode takes the LAST `picture_size` = xlarge (≤ 1280 px) · extended-metadata etag + `cache_ttl` +
per-entity 304 unused (official warm start: 42 POSTs vs 306 cold) · one uint revision formatter; accept 32-byte
(56-hex) revisions ("Your Clips") · playback path sends single-URI `EPISODE_V4` POSTs incl. duplicates 28–40 ms apart
(`Spotify.Audio.cs` ~559–580) · push-loop guard for rolling lists · rootlist `truncated` paging (official asks
`length=120`) · `ban`/`artistban` sets not synced · version header `1.2.94.583.g60394bd5` vs bare `1.2.96.518`
(`Identity.DesktopSemver`) · "web" identity tuple (`App-Platform: WebPlayer`) on ~30 ops vs the official's desktop
tuple everywhere (except `spotify-app-version: 896000000` on getAlbum/queryArtistOverview) · autopodcast body shape ·
herodotus dealer pushes (`hm://herodotus/…`) unhandled · official hydrate limit 1000 · transcript grammar (section is
EITHER a title OR a sentence) · Your Episodes = playlist `37i9dQZF1FgnTBfUlzkeKt` (format `listen-later`).

### 5.5 Corrections to our plans and assumptions
- `revision=0,<hash>` = a Spotify-generated list, NOT "no baseline" (D plan §3.8 wording is wrong).
- Head-only per-playlist pushes are echoes of the client's OWN reads, not a daily refresh.
- `/diff` answers by list kind: user playlists trivial diff · editorial/daily/descripto 304 · mixes, liked-songs,
  listen-later full contents at the same revision.
- `SelectedListContent` field 23 = 1 ⇔ the list has no `format` (not "user-owned"); `MetaItem` 7 IS `Capabilities`.
- Extended-metadata cap = 300 entity requests per POST; a split ask shares one `task_id`; cold official yardstick =
  283 POSTs in 38 s, median 17.5 uris.
- **Podcast plan**: the desktop client does NOT have `queryShowMetadataV2`, `getEpisodeOrChapter`,
  `queryNpvEpisodeChapters` at all. Show/episode data come from extension kinds — 37 rating (`spotify.ratings.PodcastRating`),
  3 topics, 54 HTML description, 179 images/colours, 21 transcript availability, 182 duration/explicit — plus
  `queryNpvEpisode` (rotated to `b1cb5ba8…`, now `{uri, includeEpisodeContentRatingsV2:true}`) and chapters from
  `GET /playlist/v2/list/podcast-chapters/{episode}` + kind 178 titles. Plan §5.9 needs rewriting on this basis.
- **P10 unblocked**: comment writes captured — `addComment` `504a54dc…` `{entityUri,text}`, `addCommentReply`
  `d4046f61…` `{commentUri,text}`, `addCommentReaction` `0af9821f…` `{commentUri,reactionUnicode}` (+ delete ops in the
  bundle table); rating = `POST spclient.wg…/ratings/v1/rating/show/<show uri>?market=from_token` `{"rating":N}`
  (upsert), read back via kind 37. Comment semantics: one top-level comment per user per episode (eligibility gates
  the composer, not the tab), page size 25, no write returns an id, never retry except 401.
  `findings-podcast-pathfinder.md` §0 and §4.

---

## 6. Open — owed next

### 6.1 Build risks flagged by the agents (first build)
`List.InsertRange(int, ReadOnlySpan)` and generated proto names `Unknown7CapabilitiesShape`/`Unknown9`/`Unknown16`/
`Unknown17`/`Unknown23UserOwnedHint`/`AddBeforeItem`/`AddAfterItem` (D3-pure) · `Store.Open`'s `NotNullWhen` flow
(D1-S1) · the P2-M Mask-bit/trampoline additions · anything calling `Store.FileGlob` (deleted) · `Playback.Host.LoadHost`
lost its `why` parameter (one caller, updated).

### 6.2 Fixes not yet written (the three stopped scopes + the rest of §5)
- **Connect**: ack every command via put-state `last_command_message_id` (§5.2-2); final inactive PUT on quit (-3);
  ids minted at load, `session_command_id` = command id (-4); put-state fidelity + registration (§5.3-13); never swallow a remote verb on a faulted item (§5.2-6); distrust a bogus external Content-Length (§5.2-7).
- **Envelope + lists**: list headers (§5.3-10), timeZone (§5.3-11), revision formatter + 56-hex (§5.4), cover size,
  rootlist `length=120` + `truncated`, `Identity.DesktopSemver`.
- **Show episode list** via `playlist/v2/show/{id}` (§5.2-5).
- Episode number/season 89/88 (§5.3-12); first-open full read (§5.3-9); gabo (§5.3-14); the Wavee-vs-official list (§5.3-16); the §5.4 list; the podcast plan
  rewrite per §5.5.

### 6.2b P3 leftovers (as-built "P3 (show reader)" -> Needs)
Loc keys (lowercase filter/sort words; playLatest, playFirst{n}, resumeLeft{left}, follow, fact labels, trailerTitle,
trailerWhy, bellHint) · an `ActionIcons` check key · `CopyLink` for show/episode targets · Detail frame wash/accent from
the show's tone (today cover-first) · skeleton counts hidden satellites · split `Show.Page.cs` (1200 vs 900) /
`Show.UI.cs` (650 vs 380) into `Show.Rail.cs` + `Show.Head.cs` · engine focus-within for the hover-only row cluster.

### 6.3 OPEN items recorded in `as-built-20260919.md`
Optimistic edit on a list still loading in pages can re-arm a revision over mixed rows · Show/Artist/Album/Track/
Playlist/User/Concert shapes still reload a whole row at the highest group authority · the library word-rail
underline now slides 167 ms (was an 83 ms fade) · `Completed` vs `Played(pct)` disagree for < 25-min episodes (P3 feeds
`Completed ? 1f : Pct`) · `EpisodeFields.Detail = 1<<9` collides with Progress (P5) · fromMs 0 = "resume" (P5 "start
from beginning" needs its own intent) · first hydrate can create up to 1021 (→ 1000) progress-only rows.

### 6.4 Verification owed (the owner runs; logs to read)
- Build Debug + Release, `Wavee.Tests`, engine gates (the engine did not change this session after `feb0fb0a4`).
- D1: a launch creates `library.<fp>.db`, legacy `library.db*` reaped, `store.open` line, second launch hands off.
- D2/D3: relaunch + open a playlist ⇒ one `/diff` (`list.replay` line), no `PlaylistRead` (needs §5.3-9 fixed).
- D4: `fetch.send` lines; far fewer extended-metadata POSTs; no 401s before login.
- B4/B6: `ap.exit` / `ap.keepalive` lines; AP reset rate vs 09-19 (12 resets in 3.6 h, 8 within 11–57 ms of our
  immediate pong).
- Podcast: the Diagnostics ▸ Runtime ▸ Podcast wire check (hashes for the ops the desktop does not have).
- Hashes: Home, a Playlists search, a Profiles search answer 200 with the new documents.

### 6.5 Git / issues / docs
Nothing committed since `41782408`. Issues + CHANGELOG + `Fixes #n` owed for every wave (plan §9 lists of the D and
podcast plans + the playback plan's B list) — each `gh` call approved by the owner first. D5 docs (the D plan's
§3.8/§3.9 corrections in §5.5 above; `09-show-episode-module.md` chapter amendments for P) not done. Delete
`C:\WAVEE\wavee-captures\fresh-client-2026-09-19\vc*\extracted\` (credentials) when the captures are no longer needed.
