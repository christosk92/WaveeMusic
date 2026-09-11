# Handoff — 2026-09-10 (late): branch, issue and worktree state

Supersedes the bookkeeping half of `handoff-20260910-release-split-and-lyrics.md`. That document's §7 (the
lyrics investigation) is **partly retracted** — see "Corrections" below. Everything else in it still stands.

**Nothing is released.** No tag, no signing, no feed, no Store submission. `main` is `wavee-v0.2.8` exactly.

---

## 1. Branches — the stack

| Branch | Ahead of `main` | PR | Pushed? | What it is |
|---|---|---|---|---|
| `main` | — | — | — | `wavee-v0.2.8`. `Wavee.Version.props` still `0.2.8` / `Breaker` |
| `release/0.2.9-perf` | **11** | **#124** draft | in sync | the 0.2.9 content. Cut from `main`, NOT part of the stack below |
| `feat/library-v3-1` | 18 | **#123** draft | in sync | 0.3.0 content — Library V3.1 + 15 bug fixes |
| `feat/catalog-state` | 20 | **#122** draft | in sync | archive, **do not merge**. Contains `feat/library-v3-1` |

Containment: `fix/audio-disk-cache-tests` ⊂ `feat/library-v3-1` ⊂ `feat/catalog-state`.
`release/0.2.9-perf` is **not** in any of them.

**Dead local branches — safe to delete.** `fix/audio-disk-cache-tests`, `fix/playlist-sync-convergence`,
`fix/store-dual-architecture` (0 ahead), `fix/layout-nav-pass-0.2.8` (0 ahead, 1 behind),
`chore/release-issue-linkage` (upstream gone, 17 behind).

### Engine — `christosk92/fluent-gpu`

- **PR #26** `feat/ultra-fast-engine` (`fce96119`) — not a draft, **MERGEABLE**, and it gates everything.
  `f4b6b182` moved 21 call sites to the new generic `PagedShelf.Create<T>`; the non-generic overload is gone, so
  **`release/0.2.9-perf` cannot build against engine `origin/main`**.
- Engine local `main` is **1 commit ahead of origin, unpushed**: "detached video window: borderless frame, live
  flyout anchors, toast scoping".
- The PR body reworks `PcmAudioSession`'s output path and `AudioClockPosition`, but says **nothing about output
  device switching** — do not assume it closes #112.

---

## 2. What 0.2.9 now contains — 11 commits

```
502a290b lyrics: rebuild the rows when the word-synced upgrade lands   (+ the 0.2.9 CHANGELOG section)
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

**Closes on merge: #111, #125, #126, #127, #128.** The CHANGELOG's `## [0.2.9] - unreleased` section exists now
and cites exactly those five. The other six commits carry no issue ref by design → "Other changes".

### Why the trailers sit where they do

`2e9d4ad6` (#127) and `60deb75a` (#128) were already pushed under PR #124 with no trailer, and #126's fix
(`3610d3ce`) had no issue at the time either. Rather than rewrite a pushed PR branch, `502a290b` carries
`Fixes #126 / #127 / #128` alongside its own `Fixes #125`. The `issue refs` gate is satisfied and GitHub closes
all four on merge. **Do not "tidy" this with a rebase** — it would force-push the PR branch for no gain.

### The gate is symmetric — read this before touching the CHANGELOG

`Wavee.Release.psm1:627-667`. `MissingInGit` = the CHANGELOG cites an issue **no commit in range fixes** → hard
fail. `MissingInChangelog` = a commit fixes an issue the CHANGELOG **never cites** → hard fail. So the ref set in
the CHANGELOG must equal the set of issues closed by commits in range. Bullets with **no** ref are legal (soft
`issue coverage` warning only). Run `-DryRun` and read the `issue refs` row before cutting.

---

## 3. Corrections to the previous handoff

Its §7 named two headline findings. `502a290b` shows **both were probe artifacts**, not app faults:

- **"P4 karaoke wipe BROKEN — revert the LyricLineView restructure"** — the probe folded **line-synced** rows,
  which have no wipe *by design*, into "voice-frames missing wipe". It now excludes them and can report
  `NOT MEASURABLE`. There is no LyricLineView regression.
- **"`pin HELD` = 0 = the sibling-defer path is dead"** — that counter only increments on the full-record path
  inside a user-scroll hold window, so **0 is the expected reading** when the untouched lyrics subtree replays
  its cached span. It was never evidence the blur-pin cache is dead.

**What was real, and is now fixed:** the "syllable lyrics work sometimes and sometimes not" report. A row freezes
its `LyricLine` at mount; the upgrade gate compared **text only**; the aggregator answers line-synced first and
publishes the word-synced upgrade a second later with byte-identical text (`text=1.00 lcs=69/69`), so the rows
were kept holding a line with no syllables for the whole track. `LyricsRowShape` now compares everything a row
freezes. **#125.**

**Still open, engine-side:** blurred lyric rows miss their blur pin on ~4.5 of ~9 layers per frame while in
flight — the compositor never pin-hits a region-clamped strip while `InMotion`. Quantizing the translate to whole
device pixels was tried and changed nothing. That is an engine question, not `LyricsView`'s.

---

## 4. Issues

**Open: 27 on `0.2.x Breaker`, 5 on `0.3 Crest`, 1 on `Future`.**

### Filed this session

| # | Title | Labels |
|---|---|---|
| #125 | Karaoke wipe and glow are dead for a whole track when the word-synced upgrade has identical text | `type: bug`, `area: lyrics` |
| #126 | A rename or new profile picture never reaches a running client | `type: bug`, `area: shell`, `area: auth` |
| #127 | Placeholder art tiles stay flat grey after their cover grading lands | `type: bug`, `area: sidebar`, `area: shell` |
| #128 | Play/pause remounts the entire equalizer subtree | `type: perf`, `area: player` |

Also transferred from `fluent-gpu` (reporter **hovrawl**), reopened here and triaged: **#111** (avatar, fixed in
0.2.9), **#112** (audio doesn't switch on device change, `P1`), **#113** (audio lag when the device lags,
`0.3 Crest`). The transfer left redirect stubs on the old fluent-gpu numbers.

Moved to `0.3 Crest` because they are catalog-branch artifacts and do not exist on the shipped line: **#114**
(row drip 1/frame), **#119** (`DataCommitQueue` publication gate), **#120** (`StartWindowLadder_IsIdempotent` —
verified **passing** on 0.2.9).

**#116** and **#121** carry comments naming the commits that landed against them. Both stay open deliberately:
#121 is an umbrella with a numeric target and the perf tour has never been run; #116 measures *per-navigation
allocation* while `38ed49bc` addresses *retention*. Re-measure both after fluent-gpu#26 merges.

### Recommended for 0.2.9, not yet done

1. **#115 — jumplist on the window thread.** 45-135 ms per launch on the Window phase; every other step in that
   ladder is under 8 ms. Move it to Worker or defer past first frame. Best value/risk on the board.
2. **#92 — hero auto-fit runs an 8-probe search twice per layout pass, every frame.** App-side fix is a definite
   `Width`/`MaxWidth` on `PlaylistInlineEdit.cs` ~:539-546. Correct fix regardless of whether the engine's
   text-cache ring already masks it — and it removes the "verify on the real hero" step.
3. **#95 — verify and close.** Its six tests were re-run on `release/0.2.9-perf`: **12/12 pass**, and the full
   suite is clean apart from the flake below. Confirm on `wavee-base` (clean `main`), then close.

### Deliberately out of 0.2.9

**#117** (cold reveal `readinessAtFirstFrame=pending`) — a readiness-gating change on the reveal path, in the
territory the catalog rework owns; too much blast radius for a release whose point is isolating variables.
**#116**, **#112**, **#113** — re-measure/re-test after the engine merges.

### Bookkeeping problem — fix before cutting

**#96–#110 (15 issues) sit on `0.2.x Breaker` but are all fixed on `feat/library-v3-1`, the 0.3.0 branch.** Cut
0.2.9 with them there and the milestone reads as permanently unfinished and the notes mislead. Move them to
`0.3 Crest`.

### Not yet filed

**Order-dependent test flakes.** Two now: `CoverColorPlaneTests.FailedBatch_IsRetriedByTheNextRender` and
`LyricsDiskCacheTests`. Both fail in a full run and pass in isolation (12/12 and clean respectively). The release
script runs the suite as a hard gate, so each flake is a coin-flip on cutting a release.

---

## 5. Worktrees

### App — `christosk92/WaveeMusic`

| Path | Branch | Dirty | Notes |
|---|---|---|---|
| `C:\WAVEE\WaveeMusic` | `feat/catalog-state` | clean | the user's main checkout |
| `C:\WAVEE\wavee-0.2.9` | `release/0.2.9-perf` | clean | 0.2.9 is built here; **PlayPlay junction linked** |
| `C:\WAVEE\wavee-base` | `main` | clean | A/B baseline; PlayPlay linked; builds against either engine |
| `C:\WAVEE\waveemusic-store-bundle` | `fix/store-dual-architecture` | **11 files** | see below |
| `C:\WAVEE\waveemusic-store-0.2.8-x64` | detached @ `38df2120` | clean | 0.2.8 Store arm |
| `C:\WAVEE\playback-verification\waveemusic` | detached @ `30e5e757` | 69 files | scratch |

### Engine — `christosk92/fluent-gpu`

| Path | At | Purpose |
|---|---|---|
| `C:\WAVEE\fluent-gpu` | `feat/ultra-fast-engine` (`fce96119`) | what every app worktree resolves to by default |
| `C:\WAVEE\fluent-gpu-main` | detached `b946fc373` | the 0.2.8 engine; old-engine A/B arm |
| `C:\WAVEE\fluent-gpu-store-validation` | detached `b946fc373` | Store validation arm |
| `C:\WAVEE\playback-verification\{fluent-gpu,engine-baseline}` | detached `1f906c906` | playback A/B |
| `C:\WAVEE\fluent-gpu\.claude\worktrees\agent-a5e96cdc…` | 4 weeks stale | leftover agent worktree; prune |

`$(EngineRoot)` is `$(MSBuildThisFileDirectory)..\fluent-gpu` (`Directory.Build.props:36`), so each app worktree
resolves to the **sibling** engine checkout. Override per shell with `$env:EngineRoot` for an A/B.

### Work at risk

**`C:\WAVEE\waveemusic-store-bundle` holds 11 uncommitted files and its branch is 0 commits ahead of `main`.**
The entire Store dual-architecture bundle feature exists only as working-tree changes:
`ops/release/Wavee.StoreSubmission.psm1` (new), `ops/release/tests/Wavee.Store.Bundle.Tests.ps1` (new),
`ops/release/tests/Wavee.StoreSubmission.Tests.ps1` (new), `docs/plans/wavee/store-bundle-implementation.md`
(new), plus edits to `wavee-store-submit.ps1`, `Wavee.Store.psm1`, `Wavee.Store.Tests.ps1`,
`ops/release/README.md`, `docs/guide/releasing-wavee.md`, `docs/guide/microsoft-store-onboarding.md` and
`.claude/skills/releasing/SKILL.md`. One `git checkout .` loses all of it. **Commit it.**

---

## 6. Builds produced this session

| Branch | Path | Size |
|---|---|---|
| `release/0.2.9-perf` | `C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe` | 42.59 MB |
| `feat/catalog-state` | `C:\wavee\WaveeMusic\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe` | 46.46 MB |

Both are `v0.2.8-dev` (`WaveeChannel=dev`) — the loose-publish path: About and the crash header say development
build and the update checker skips comparison. The 3.87 MB delta is the catalog/query system.

**Before launching the 0.2.9 exe:** `%LOCALAPPDATA%\Wavee\library.db` currently holds the **catalog** schema, and
a hydration-facade build throws `SQLite Error 1: 'no such table: entity'` against it — Home renders "Something
went wrong". Move `library.db*` aside; credentials live in `store.json`, so it costs a re-sync, not a re-login.
This is also a real forward hazard: once 0.3.x ships the catalog schema, a user rolling back to 0.2.x hits it.
Worth an issue and a schema-version guard.

---

## 7. Verification status

- `release/0.2.9-perf`: Release NativeAOT arm64 publish **clean**; `Wavee.Tests` **7415 passed, 1 skipped**, 1
  order-dependent flake (see §4).
- **Not run:** Debug build on this branch since `502a290b`, the manual pass, the before/after perf tour.
- Engine gates (`dotnet build src/FluentGpu.slnx` Debug + Release, VerticalSlice) were last green at the previous
  handoff: 1478 checks in both configurations.

---

## 8. Open decisions

1. **Merge fluent-gpu#26.** Nothing builds against engine `main` until it does. Push engine `main`'s 1 unpushed
   commit while you are there.
2. 0.2.9 is framed as perf-only but carries four bug fixes (#111, #125, #126, #127) plus a perf one (#128).
   Either reframe the release notes or move them to 0.3.0. The CHANGELOG as written **reframes** it.
3. Pull in #115 and #92? Verify and close #95?
4. Move #96–#110 to `0.3 Crest`.
5. Version bump (`Wavee.Version.props` → 0.2.9) — still not done, deliberately.
6. Commit the Store bundle work before it is lost.
7. Run the manual pass and the perf tour. #121 does not close without them.
