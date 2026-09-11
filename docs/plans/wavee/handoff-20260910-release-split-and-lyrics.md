# Handoff — 2026-09-10: the release split, 0.2.9, and the lyrics investigation

Copy-paste this to the next agent. Everything below is committed and pushed unless it says otherwise.

---

## 1. Where everything lives now

Two repos, several branches. **Nothing is released.** No tag, no signing, no feed, no Store submission.

### App — `github.com/christosk92/WaveeMusic`

| Branch | Contents | PR |
|---|---|---|
| `main` | `wavee-v0.2.8` exactly — zero unreleased commits | — |
| `release/0.2.9-perf` | 10 commits: perf + two account fixes + instrumentation | **#124** (draft) |
| `feat/library-v3-1` | 18 commits: Library V3.1 + bug fixes → the 0.3.0 content | **#123** (draft) |
| `feat/catalog-state` | **archive**: the whole former working-tree pile (A+B+C+D below) | **#122** (draft, do not merge) |

### Engine — `github.com/christosk92/fluent-gpu`

| Branch | Contents | PR |
|---|---|---|
| `main` (`b946fc373`) | what 0.2.8 shipped against | — |
| `feat/ultra-fast-engine` | 3 commits: Operation ultra-fast P0–P4 + P8, compositor animations, layer pool, upload arena, playback rework | **#26** |

**The engine PR must merge before any app release** — `Directory.Build.props:36` resolves `$(EngineRoot)` to the sibling checkout and `ops/release/wavee-release.ps1` has no engine-cleanliness gate, so the app builds against whatever is checked out.

### Worktrees on this machine

| Path | Branch | Purpose |
|---|---|---|
| `C:\wavee\waveemusic` | `feat/catalog-state` | the user's main checkout |
| `C:\wavee\wavee-0.2.9` | `release/0.2.9-perf` | where 0.2.9 was built; **has the PlayPlay junction linked** |
| `C:\wavee\wavee-base` | `main` | A/B baseline; PlayPlay linked; built Release JIT |
| `C:\wavee\fluent-gpu` | `feat/ultra-fast-engine` | the engine the app resolves to by default |
| `C:\wavee\fluent-gpu-main` | detached `b946fc373` | old-engine arm of the A/B |

`link-playplay.ps1` is gitignored and copied into each worktree; it links relative to its own location, so it must be copied in before running.

---

## 2. What was decided (do not re-litigate)

- **0.2.9 first, perf-only, cut from `main`**, then **0.3.0** with Library V3.1. Isolates variables: a regression in 0.2.9 is unambiguously the engine or one of ten small commits.
- **Engine audio/playback work ships** with the engine.
- **Catalog/query rework is deferred** to 0.3.x. It "has a lot of issues" and is not wanted in a release.
- Verification bar for 0.2.9: **gates + a manual pass + a before/after perf tour**.

The plan file: `~/.claude-work/plans/i-want-clear-seperation-generic-pumpkin.md`.

---

## 3. The 0.2.9 branch — 10 commits

```
06607f04 diagnostics: always-on frame cost log and the perf tooling that reads it
3610d3ce account: apply the profile pushes Spotify already sends
c9a336c2 account: show the profile picture instead of its URL
79efddc6 diagnostics: startup timeline anchor, frame-budget levels, performance counters
60deb75a equalizer: stop remounting the whole subtree on every play/pause
2e9d4ad6 art: repaint a placeholder tile when its cover grading lands
f4350874 detail: compare a row shape by value, not by array reference
74ae676c player: read the playhead as a bound channel, not a component subscription
38ed49bc shell: keep three pages alive, not eight
f4b6b182 shelves: migrate PagedShelf.Create to the engine's items-based overload
```

Notes worth carrying forward:

- **`f4b6b182` is mandatory, not optional.** The new engine dropped the non-generic `PagedShelf.Create`; 21 call sites across 9 files had to move to `Create<T>(IReadOnlyList<T> items, …)`. This is the *only* API break the engine introduces — an engine-wide name-level scan found 3 other removals, all `internal` to `FluentGpu.Windows`. **Consequence: `release/0.2.9-perf` cannot build against engine `origin/main`.** Use `C:\wavee\wavee-base` (app `main`) for any old-engine A/B.
- **`f4350874` (RowShape) will not measurably help.** `TracksFor` caches its arrays, so the array is usually reference-stable on this branch. It closes a latent bug; do not sell it as a win.
- **`c9a336c2` fixes #111** — `PersonPicture.Create` takes `initials` first and the photo as named `imageSourcePath`; `ProfileMenu` passed the URL positionally, so the control rendered `https://i.scdn.co/image/…` as text inside the avatar circle. Every other call site in the app was already correct.
- **`3610d3ce`** adds the `hm://identity/user-profile-changed` dealer route. See §6.
- **`06607f04`** is what makes measurement possible at all — before it, this branch emitted no frame lines whatsoever.

**Verification:** Debug + Release clean, **7410 tests pass**, 1 skipped, 0 failed. Engine: Debug + Release clean, **1478 VerticalSlice checks in both configurations**.

**Not done on purpose:** no `Wavee.Version.props` bump (still `0.2.8` / `Breaker`), no `## [0.2.9]` CHANGELOG section. Both are release bookkeeping and the user asked for nothing to be released.

---

## 4. What was attempted and reverted — the app playback rework

The user asked for it in 0.2.9. **It cannot ship without the catalog rework**, and this is settled, not a judgement call:

- `Backend/PlaybackQueueProjection.cs` opens `using Wavee.Backend.Catalog; using Wavee.Backend.Queries;` and describes itself as *"One catalog join for session, Connect and queue display"*. It is consumed by **both** `PlaybackProjection` and `PlaybackSession`, so the new playback path projects its queue from the catalog repository.
- Nine playback test files sit on `PlaybackCatalogTestBase`, itself catalog infrastructure.

Ported far enough to prove it: 32 files brought across took the build from **312 errors to 198**, and the residue was exactly the catalog-native queue projection, `PlaybackBridge`'s query binding, and that test base. Reverted rather than left half-applied. It belongs with #122.

The **engine** half of the audio work is unaffected and is in fluent-gpu#26.

---

## 5. GitHub state

**0.3.0 (`feat/library-v3-1`) closes 16** via `Fixes #n` trailers already in the commits:
`#95 #96 #97 #98 #99 #100 #101 #102 #103 #104 #105 #106 #107 #108 #109 #110`

**0.2.9 closes:** `#111` (avatar). Also very likely **#92** — the engine's A1 change turns the single-slot `TextMeasureCache` into a 2-entry ring, and `gate.layout.text-cache-ring` reproduces #92's exact scenario (`firstPassMisses=2` → `secondPassMisses=0 secondPassShapes=0`). The app-side `Width`/`MaxWidth` fix in `PlaylistInlineEdit.cs` is **not** in 0.2.9, so verify on the real hero before closing.

**Issues filed this session:** #114–#121 (perf epic + children).

**Three of those are mis-scoped and should move to `0.3 Crest`/0.3.x** — they were measured on the catalog build and describe code that is not in the shipped line:

| Issue | Why |
|---|---|
| **#114** row drip 1/frame | A regression the *catalog branch* introduces. `main` has `staggerCold = false` and engine `c5fa6d22b` shipped in 0.2.8. Does not exist in 0.2.9. |
| **#119** publication gate | `DataCommitQueue` is catalog-only code. |
| **#120** `StartWindowLadder_IsIdempotent` | **Passes** on 0.2.9 (verified). Fails only on the catalog branch. |

**Pending, needs the user's approval (every modifying `gh` call does):**
- file issues for two 0.2.9 fixes that have none — the grey placeholder tiles (`2e9d4ad6`) and the equalizer remount (`60deb75a`)
- file an issue for the stale profile (`3610d3ce`) so the CHANGELOG can cite it
- move #114/#119/#120 to `0.3 Crest`
- add `Fixes #92` once verified

---

## 6. `hm://identity/user-profile-changed` — decoded

Spotify pushes this whenever the account name or picture changes anywhere. Nothing subscribed to it, so a rename never reached a running client. A dealer capture of one rename shows **five** messages, all `handled=false`.

Payload is base64 protobuf, and matches `UserProfilePayloadDecoder` exactly:

| Field | Meaning |
|---|---|
| `1 { 1: string }` | user id |
| `2 { 1: string }` | display name |
| `3 { 1:w, 2:h, 3:url }` *(repeated)* | images — 64×64 and 300×300; the decoder keeps the largest |
| `9`, `10 { 1: 1 }` | flags; `10` appears only alongside images |
| `11 { 1: varint }` | changed across the rename; unknown, ignored |
| `24 { 1: string }` | opaque etag, ignored |

**The service pushes per keystroke.** The captured rename was `"Christos"` → `"Christos"` → `"Chrr"` → `"Chrr"`+images → `"Chris"`+images across 56 s. Hence `TrailingCoalescer` (leading + 750 ms trailing).

`UserProfilePush` holds two rules, engine-free and tested against the captured bytes verbatim:
- the id in the **payload** authorises the apply, never the fact the message arrived;
- **an image-less push updates the name and leaves the picture standing** — three of five pushes have no image because the picture had not finished processing; assigning wholesale would blank the avatar for the length of the rename. Cost: a picture *removal* is not reflected until the next login.

Dealer archive lives at `%LOCALAPPDATA%\Wavee\logs\dealer\dealer-<date>.bin` + `.idx.ndjson`; the idx gives `off`/`n` to slice payloads out of the bin.

---

## 7. The lyrics investigation — RESOLVED 2026-09-10 (second session)

**Symptom (user):** syllable-by-syllable lyrics are not smooth, make the app slow at line changes, and work on some
plays but not others.

### 7.1 "Works sometimes and sometimes not" — the same-shape upgrade froze the rows (FIXED, app side)

`LyricLineView` freezes its `LyricLine` at mount. `PrepareDocument` kept the mounted rows whenever the new document had
the same per-line **text** (`SameLineShape`). The aggregator's normal sequence is: Spotify's **line-synced** transcription
wins inside the first-hit grace window, and a **word-synced** document (Kugou / Musixmatch / AMLL) arrives ~1 s later
through `LyricsUpgraded` with **identical text** — the reranker scores every candidate against the Spotify reference, so
`text=1.00` is the common case. Today's log, verbatim:

```
[lyrics] track=2tpWsVSb9UEmDRxAl1zhX1 winner=spotify sync=Line     score=0.877 text=1.00 ... (ref-align lcs=69/69)
[lyrics] track=2tpWsVSb9UEmDRxAl1zhX1 winner=kugou   sync=Syllable score=0.903 text=1.00 ... (ref-align lcs=69/69)   +1.0 s
```

Same text on all 69 lines ⇒ rows kept ⇒ every row still holding a `LyricLine` with **no syllables** ⇒ no karaoke wipe,
no held-note glow, for the whole track. The next play of the same track reads the disk cache, which the background
continuation overwrote with the word-synced document ⇒ works. Sugar (`lcs=60/61`, one line differs) remounted and
worked on the first play, which is exactly the probe's INTACT (Sugar) vs BROKEN (Dutch track) split. The code comment
called this the "ACCEPTED RESIDUAL"; it was the bug.

**Fix:** `src/apps/Wavee/App/LyricsRowShape.cs` (engine-free, pure) — rows survive only when every field a row freezes
is identical: text, `IsWordByWord`, every syllable, translation, romanization. Anything else bumps `_docEpoch` and
remounts (the path that was always correct). `LyricsRowShapeTests` pins the captured scenario. Uncommitted in
`C:\wavee\wavee-0.2.9`; `LyricsView.cs` is byte-identical on `main`, `release/0.2.9-perf`, `feat/library-v3-1` and
`feat/catalog-state`, so the same diff applies anywhere.

### 7.2 The probe misread itself — three of its headline findings were not findings

- **P4 "karaoke wipe/glow BROKEN — REGRESSION, revert the LyricLineView restructure"** counted every voice frame whose
  row had no `GlyphWipe` / no glow node. A **line-synced row has neither by design**. The 67/81 on the Dutch track were
  rows mounted line-synced (§7.1). Fixed: P4 now counts word-by-word rows only and reports line-synced voice frames
  separately (`LyricsView.ProbeLineIsWordByWord`).
- **"pin HELD = 0 ⇒ the sibling-defer path is dead"** read `BlurHoldCandidateCount`, which increments only on the
  full-record path **inside a user-scroll hold window** (`SceneRecorder.cs` `holdBlur = … && (globalBlurHold ||
  userScrollActive) && policy != Normal`). It is structurally 0 during the programmatic cascade — a VerticalSlice gate
  (`gate.lyrics.programmaticFollowKeepsDoF`) asserts exactly that — and 0 during a main-page wheel because the untouched
  lyrics subtree replays its cached span before the counter. Pin hits/misses are the `d3dBlurMiss` column, which was
  **0.0 per frame** during every main-scroll phase on both engines. The hold/pin code (`BlurPinKey.cs`, the recorder gate,
  `D3D12Device.SubmitWithLayers`) is **byte-identical** between `b946fc373` and `feat/ultra-fast-engine`. Not a
  regression; wording fixed in the P2 summary line.
- **"105 → 30,013 non-monotone growths, ~285× for 2.4× the lines"** compared a valid run against an invalid one. In
  `lyr-armA-newengine` the app **stopped producing frames at P1 line 5** (row 257 of the CSV: `recordMs=0, presented=0,
  blurCandidates=0` for the rest of the run, including 0 of 180 presented frames while the probe wheel-scrolled the main
  page). The offset froze at 433.9 because no frame ever drained the scroll port; `ProbeStep(forceVisual)` then re-ran
  `ScrollActiveIntoView` every frame, saw the offset short of target, and re-armed the cascade (comp += delta) on every
  frame — that is the 30,013. Most likely the window was occluded/minimized and the new engine parks rendering
  (`_renderVisible`). **That arm is invalid.** Also: `wavee-base` (app `main`) does **not** build against the new engine
  (21 `PagedShelf.Create` errors) — the handoff's A/B recipe cannot have produced it.

### 7.3 What IS real, with numbers (same track, same build, before → after)

Runs: `%TEMP%\lyr-029-baseline` (0.2.9 Release JIT, unmodified), `lyr-029-fixed`, `lyr-029-final` (the code as left),
all on *Moves Like Jagger* (94 lines), new engine; `lyr-B1-oldengine` (Sugar, 85 lines, old engine) as the reference.

| | old engine | new engine, baseline | new engine, final |
|---|---|---|---|
| handoffs whose latch landed on the latch frame | **21/21** | 14/21 | 19/21 |
| non-monotone `\|comp\|` growths (all from late latches + forceVisual re-arm) | 0 | 3073 | 61 |
| blur-pin misses per frame, in-flight (f6–30) | 5.7 | 4.5 | 4.9 |
| blur-pin misses per frame, at rest (f40–55) | — | 3.0 | 1.4 |
| P3 idle: static frames still presented | 2 % | (aborted) | **100 %**, fence 7.2 ms |

1. **Engine: the immediate latch lands late.** `LatchViewport` posts `ScrollTo(animate:false)` = `immediate:true`,
   "resolved on the kernel's next Reclamp". On the old engine every one of 21 handoffs landed on the latch frame; on
   `feat/ultra-fast-engine` 2–7 of 21 land one or two frames later (`lyOff` unchanged on the latch frame, then jumps).
   Because `ArmCascade` has already written `+delta` compensation to every in-band row on the latch frame, the document
   visibly **drops by one row for 1–2 frames and then snaps up** — the "not smooth" at line changes. Look at
   `ScrollKernel.SetDrivenTarget`/`ApplySetFrame` (the clamp against a stale `Frame.ExtentMain` re-derives from
   `TargetRaw` on a later frame) and at when the scroll port is drained relative to layout. Engine work.
2. **Engine (both): ~5 Gaussians per frame for the whole 0.48 s flight.** ~9 blurred rows are recorded per frame and
   ~4.5–5 miss their pin every frame while moving, then hit at rest. The device's hit gate refuses a hit for a
   **region-clamped** layer while `InMotion != 0` ("a clamped strip in motion re-blurs", `D3D12Device.cs` ~3494), and
   `RegionIsClamped` is measured against the **window canvas** — the rail sits flush with the window edge and a DoF layer
   carries a 3σ halo, so most rows are clamped. Quantizing the cascade translate to whole device pixels was tried and
   changed nothing (4.5 → 4.5); reverted. The DoF σ ramp re-keys the incoming rows for its first ~200 ms on top.
3. **Engine: with the rail open and nothing moving, the new engine presents every frame** (P3: 240/240 static frames,
   7.2 ms fence each; old engine 5/240). That is the app-wide cost of an open lyrics rail. `FenceWaitMs` includes the
   present-latency waitable, so 6–7 ms is pacing plus GPU; the whole-frame GPU timestamp is not in the probe CSV.
4. **App (done): `WriteCascade` now marks `TransformDirty` only.** It marked `PaintDirty` too, which flags the row span
   content-dirty and forces a full re-record of up to 2·`CascadeWriteBand` rows per frame instead of the recorder's
   translated-span copy. Correct by construction; its CPU effect is not measurable on this engine (`recordMs` reads 0.0
   under the render-thread architecture).

### 7.4 How to run the probe without losing the run

The trap in the previous handoff still applies (audio keeps playing; restore reloads the last track **paused**). Two of
the three runs today aborted before P3/P4 for an unknown reason with exit code 0 — always check `frames=` per phase.
Keep the window in the foreground for the whole run (`SetForegroundWindow` after launch); the new engine stops
rendering when it is not visible and the run silently degrades to §7.2's invalid shape.

```powershell
$env:WAVEE_LYRICS_ADVANCE_PROBE = "1"; $env:WAVEE_PROBE_OUT = "$env:TEMP\lyr-<name>"
& C:\wavee\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\Wavee.exe     # exits by itself when the probe is done
```

Verification of the change set: `Wavee.slnx` Debug + Release clean, `Wavee.Tests` 7416 passed / 1 skipped (6 new).
`LyricsDiskCacheTests.Provider_LineSyncedDiskHit_ServesItThenUpgradesInTheBackground_…` failed once in a full run and
passed 3/3 in isolation — timing-flaky, untouched by this work.

---

## 8. Environment gotchas that cost time

- **`%LOCALAPPDATA%\Wavee\library.db` now holds the CATALOG schema** (`catalog_resource`, `catalog_scope`, `catalog_search`, `replica_*`) and has **no `entity` table**. Any build on the hydration facade — 0.2.9, `main` — throws `SQLite Error 1: 'no such table: entity'` on every hydration batch, and Home renders "Something went wrong". Not a bug in those builds; a shared-profile collision. Credentials live in `store.json`, so moving `library.db*` aside costs a re-sync, not a re-login. **This is also a real forward hazard: once 0.3.x ships the catalog schema, a user rolling back to 0.2.x hits exactly this.** Worth an issue and a schema-version guard.
- **A running Wavee holds the publish output** — the first AOT publish failed on the file lock. Close with `CloseMainWindow()`, never kill (a killed process leaves a stale run marker that fakes a crash on next launch).
- `Select-Object -Last N` on a publish discards the error detail; use `Tee-Object` to keep the full log.
- The Debug/Release JIT exe is at `src/apps/Wavee/bin/Release/net10.0/Wavee.exe`; the AOT publish at `…/win-arm64/publish/Wavee.exe`.
- Publishing steals focus for ~90 s; ask before touring.

---

## 9. Open decisions for the user

1. Merge fluent-gpu#26 — nothing builds against engine `main` until it does.
2. Approve the pending `gh` changes in §5.
3. 0.2.9 is framed as perf-only but now carries two account fixes (#111 + the profile push). Either reframe the release or move them to 0.3.0.
4. Version bump + CHANGELOG for 0.2.9, when ready.
5. The manual pass and perf tour, neither of which has been run.
