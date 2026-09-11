# Wavee performance handoff — paused 2026-09-09 11:36 CEST

## Start here

The user explicitly requested a pause because credits are running low. Resume only when the user asks. Nothing was committed, pushed or released. The performance goal is **not achieved**. No foreground tour, profiler, debugger, build or test is currently running for this task. Ordinary IDE/build-server processes remain; do not kill them.

Repos: `C:\wavee\waveemusic` and sibling `C:\wavee\fluent-gpu`. Read both `CLAUDE.md` files and engine `AGENTS.md` first; use the engine's `.claude/skills/fluentgpu/SKILL.md` for engine/UI work. Preserve the enormous pre-existing uncommitted trees (historically 561 app files and ~130 engine files); don't reset, stash or commit. No co-author trailers. Private PlayPlay paths listed in those rules are out of scope: never read/search/edit them. Only the orchestrator builds/tests/launches; disjoint agents may edit. No environment switches or source-text tests.

User's non-negotiable targets: **zero frames over 8.3 ms, including startup; every cold AND warm reveal <=100 ms; working set <=200 MiB throughout startup and the tour** (100–200 desired, below100 acceptable). Visible content, smooth interaction and edge cases matter. Don't redefine success as skeleton visibility, primary query publication or averages. User requires objective measurements before performance fixes.

Machine: native ARM64 Snapdragon X, Adreno X1-85, Weak GPU profile, 120 Hz. AnyCPU JIT runs native ARM64. AOT `win-x64` runs emulated and is invalid for comparison. Native publish:

```powershell
powershell -NoProfile -File ops/build/publish-wavee-aot.ps1 -Arch arm64
```

Implementation plan with actual code and execution evidence:
`docs/plans/wavee/performance-measurement-and-fixes-implementation.md`.

## Immediate blocker and exact first action on resume

The native tour found a **real startup deadlock**. A repair is written but unvalidated, and its new test currently has a known compile error:

**In `src/apps/Wavee.Tests/QueryServiceLockOrderTests.cs`, around line 209, change `handle.Subscribe(...)` to `handle.Changes.Subscribe(...)`.** Agent found this during static review immediately before the user paused. It was intentionally left untouched after the pause request.

Then review and test `src/apps/Wavee/Backend/Queries/QueryService.cs` and that new test file. The patch orders locks publication/catalog -> query node in `RetryUnservedDemand` and `TryReplan`. It removes catalog scope reads from inside the standalone node lock in `PublishJoin`, captures scope/online/epoch before the expensive DTO work, and rechecks epoch under catalog -> node before installing the result. A rejected epoch queues another join outside locks. Two lock-order probes and a mid-DTO-build scope-switch test are written. **None has been compiled or run.** Scrutinize account-switch/online epoch handling, discarded candidates, dependency/cold-work bookkeeping, and lease supersession. Keep O(keys) DTO work outside the catalog publication lock. Do not hide the deadlock by disabling memory sampling or adding timeouts.

Confirmed native stacks, not speculation:

- Worker: `RetryUnservedDemand -> CatalogRepository.Peek -> Monitor.Enter`, holding node lock and waiting catalog lock.
- Worker: `Project` lambda waiting node lock while inside `CatalogRepository.ReadConsistent -> DataCommitQueue.ReadConsistent`.
- UI: `MemorySampler.Sample -> DiagnosticOwnerRegistry.Sample -> CatalogRuntime reporter -> CatalogRepository.ResidentCount -> Monitor.Enter`.

Evidence: `%TEMP%\wavee-arm64-hang-stacks.txt` and `%TEMP%\wavee-arm64-aborted.log`. ARM64 CDB is available at `C:\Program Files (x86)\Windows Kits\10\Debuggers\arm64\cdb.exe`; native PDB resolves these stacks. Noninvasive capture used `-pv -p 24024 -cf <commands>` with `.sympath <publish dir>`, `.reload /f Wavee.exe`, `~* k 18`, `qd`.

The hung app PID24024 was terminated only after preserving evidence and after CloseMainWindow failed. An unclean-run prompt next launch is expected; do not call it an unexplained new crash. Tour helper PID31516 was stopped. No run-marker files were altered.

## Measurements and limits

Historical valid JIT ARM64 workload: `%TEMP%\wavee-perf-tour-run2.md` / `.log`, `wavee-tour-31644.gcdump`. Nav windows report 38 slow frames, scroll windows 6 (overlap: don't add as a unique total). Worst navigation frame45 ms, startup265 ms, final WS1183 MiB. Recorded cold reveals109–119 ms fail. Recorded warm6–31 ms do **not** prove global pass: several routes lacked markers.

Native ARM64 AOT publish succeeded; executable46.38 MiB:
`src/apps/Wavee/bin/Release/net10.0/win-arm64/publish/Wavee.exe`.
Build log `%TEMP%\wavee-arm64-aot-baseline-build.log`. **This binary predates the query deadlock and lyrics repairs; republish after verification.**

Its tour froze after roughly four seconds. This is INVALID as a complete baseline, but startup already had known breaches: primary-content reveal **266.4914 ms**, worst recorded paint about114.2 ms, observed WS later about628 MiB (initial sample290.6 MiB). Full native baseline remains pending.

The user was asked asynchronously when Wavee could remain foreground for roughly four minutes, with options to run now or continue fixes until they give a time. **No scheduling answer was received before pause.** On resume coordinate the foreground slot; elapsed time isn't consent. No builds/profilers/agent CPU load during a timed tour. Real touchpad run requires user participation.

Current reveal marker means primary ready content reached the active UI frame, not independently verified GPU presentation or full visible readiness. Existing primary readiness permits unresolved detail rows; some Home secondary widgets/images/entrance motion are excluded. Full-content readiness and input/present coverage remain required. FrameMs excludes portions of UI dispatch; passing current counters alone cannot satisfy the full goal.

## Changes written during this continuation

- Core `FrameSessionTotals.cs` + tests: allocation-free lifetime frame totals, raw strict8.3 threshold, refresh counts, invalid counts, worst duration.
- `App/NavigationFrameWatch.cs`: observation attached before app loop; periodic/final `session.frames`, `final=1`, exact counters; final nav/scroll flush; nav IDs; scroll owns starting route/id; escaped route args; corrected unaccounted calculation because SubmitMs already includes fence/present.
- `App/MemorySampler.cs`: exact `wsBytes`/`processPeakBytes`, sampled peak and OS lifetime peak distinguished; safe process-only `memory session-end reason=session-end` after engine disposal.
- `Program.cs`: attach before Run, finalize frame and memory after Run.
- Core `Diagnostics/PerformanceDiagnostics.cs` + tests: monotonic shared navigation clock and disposable token-owned diagnostic registry. Rewired CatalogRuntime and QueryService away from UI-only classes (previously caused nine build errors). Callbacks execute outside registry lock; duplicate runtime registrations dispose independently.
- `App/PageRevealDecision.cs`, `PageRevealWatch.cs`, tests: route/context-owned primary-content frame marker with cached activation support. Changes in QueryHooks, ContentHost, DetailPage, ArtistPage, HomePage and LibraryPage. Removed misleading query-publication reveal. Schema includes navId, route, escaped arg, `boundary=ui-frame`, revealMs, retainedUi, readinessAtFirstFrame, publishSeq.
- `ops/tools/perf-tour.ps1`, `perf-tour-analysis.ps1`, `.tests.ps1`: strict whole-session final counters; missing data INCOMPLETE; known violations FAIL even with incomplete data; all route/scroll rollups; cold AND warm gates; exact memory bytes/final sample; opt-in heap collection invalidates frame scope; Stress/Varied/UserScroll; focus and SendInput verification; actual process architecture, exe hash, revisions, power/DPI/window. First route album-A ensures returning Home is an actual transition. Fixed PS5.1 `[ushort]` to `[uint16]` and interpolation `${machineArch}:`.
- Tour still lacks dirty-tree fingerprint and playback-state capture; ScrollActive is not independently measured displacement or touchpad classification. A readiness-timeout repair was in progress when paused: it should skip futile navigation/input/heap if protocol-installed never arrives and preserve incomplete report/log/manifest. See pause supplement below for exact state.
- `ops/tools/perf-profile.ps1`, `.helpers.ps1`, `.tests.ps1`: AnalysisOnly existing artifacts; verified dump target/type/size; repeated `-c` commands; untruncated relevant root tails; exact-size byte-array samples; Free separate. Thread-aware Speedscope attribution uses embedded C# parser after PowerShell version took >10CPU minutes. Synthetic CPU_TIME/UNMANAGED_CODE_TIME/scaffolding excluded from method rankings but category totals preserved. This is sampled stack attribution, not precise OS CPU accounting.
- `Backend/Lyrics/LyricsDiskCache.cs` + tests: **written, untested** `OpenRead` uses FileShare.Read|FileShare.Delete so Windows atomic replacement isn't rejected by an active reader; source-generated DeserializeAsync streams directly. Deterministic test holds actual production reader across Line->Syllable replacement, checks old and new full documents. Cancellation preservation test added. Fixes the observed full-suite disk-upgrade failure without longer timeouts.
- `Wavee.Tests/Backend/LocalMediaProviderTests.cs`: **written, untested** fixture SetQueueAsync awaits `ObserveTracksAsync` before queue publication like production; prior single commit barrier raced preload/seed admissions. Publisher cleanup added. No production behavior change here.
- Engine `src/FluentGpu.VerticalSlice/Suites/ControlsSuite.cs`, progress.3: deterministic `probe.Context.Runtime!.Flush()` after state/width writes, retaining semantic assertions. Prior one hosted deadline flush didn't ensure parent+child settled. No engine production change in this correction.
- QueryService production fix/new concurrency tests described above remain unvalidated.

PowerShell diagnostic scripts are UTF-8 **with BOM** for Windows PowerShell5.1; preserve that.

## Verification state — don't overclaim latest tree

| Check | Last result |
|---|---|
| App Debug + Release | Passed,28 warnings each,0errors, **before latest query/lyrics/test repairs**. `%TEMP%\wavee-app-{debug,release}-measurement.log` |
| Full Wavee.Tests | 7616 passed,2failed,1skipped,total7619. Failures LocalMedia masking and lyrics disk upgrade, fixes now written but not rerun. `%TEMP%\wavee-tests-measurement.log` |
| Engine Debug + Release | Passed156warnings each,0errors. `%TEMP%\wavee-engine-{debug,release}-baseline.log` |
| Full VerticalSlice | **1396 checks pass, native exit0**, after progress.3 correction. `%TEMP%\wavee-verticalslice-confirmed.log`, `.stderr.log`. Earlier wrapper exit1 came from intentional stderr diagnostic handling; no failing assertions in measurement rerun. |
| Release Pester |304passed,0failed,1skipped. `%TEMP%\wavee-release-pester-baseline.log` |
| Tour/profile behavioral fixtures |Passed before latest in-progress tour timeout patch |
| Native ARM64 AOT publish |Passed before query/lyrics repairs; tour incomplete due proven deadlock |

No full acceptance/A-B against wavee-stable performed. No performance gate satisfied globally.

## Attribution already obtained; avoid repeating expensive dumps

Existing `%TEMP%\wavee-profile-20260909-1042.nettrace` and `.dmp` (~1.44GB) are reusable. Corrected analysis `%TEMP%\wavee-ui-profile-repaired.md`, `.raw\cpu.speedscope.json`. Old capture lacks nav windows, so thread attribution is whole-capture only. UI thread27792 has prominent GC poll, snapshot CaptureCore/CaptureTree/RetainStrings, ZeroMemory and monitor enter. Don't interpret synthetic exporter marker frames as methods.

Verified array targets in `%TEMP%\wavee-heap-raw-verified.txt`, `wavee-roots-verified.txt`:

- `01d538882bb8`16MiB -> GlyphRenderer._cpu.
- `01d53c240240`2MiB -> PixelBufferPool stack.
- `01d53d3685b8`4MiB -> completed DecodeScheduler queue.

The original handoff's claim that dumpheap columns were swapped was wrong for current source. Actual columns Address MT Size were parsed correctly; truncated root prefixes hid owners. Don't reintroduce the proposed wrong swap.

Measured historical priorities: track-row mounting ~130KB/row and repeated layout (~230 measures/row), retained scene/pages (~140MB scene columns), shelves mounting many cards, Responsive/Home remounts, image buffers, commit stalls. PagedShelf **already has bound/measured virtualization**; inspect actual branch/overscan/probes/offscreen mounting. Responsive already has equality-gated generic state. Avoid replacing working mechanisms based on stale assumptions.

Concrete memory candidates (allocation accounting, not proven AOT WS savings):

- Glyph atlas4096²:16MiB CPU +16MiB GPU texture +3x16MiB upload banks =80MiB backing. UploadIfDirty copies full16MiB. Dirty-region staging can reduce bank allocation/copying; preserve four subpixel phases, atlas size, append-only semantics, multiple flushes per submit and fence lifetime. Source GlyphRenderer.cs ~1214/1257.
- SceneRecordingSnapshot.Animation.cs dense NodePaint/interaction/brush overlays sized at scene high-water, across three snapshots. NodePaint344B =>32.25MiB overlay paint alone at32768x3. Candidate sparse values per compositor target with small node->slot map, publisher-side reservation, no render allocation. Need retarget/retained animation tests and canonical threading/scene docs.
- ScrollKernel ScrollBody dense by node ID despite ~27 viewports (~5.9–11.8MB historical). Sparse body storage candidate.
- OpacityLayerCompositor.Acquire allocates full-window BGRA targets for tiny groups (~22MiB depending window); bounded region+halo reuse candidate. Preserve nested coordinate mapping and GPU fences.
- KeepAlive retains8 full pages (live16–20k nodes); any reduction must preserve page state and <=100ms return reveal, avoid repeated allocation churn.

## Pause supplement from stopped agents

Tour timeout edits are written in `ops/tools/perf-tour.ps1` and `ops/tools/perf-tour-analysis.ps1`: failed readiness skips routes/input/heap and post-ready waits, preserves launch lines with zero steps, attempts clean shutdown even with KeepOpen, records shutdown errors/observed exit, and catches early architecture-query failure. **Unfinished:** no new policy fixtures or parser/tests run; manifest lacks new startupFailure/shutdown fields; old KeepOpen verdict still says process left open after failure cleanup and must be conditioned on actual shutdownAttempted. Verify BOM after latest patch. Both files may require corrections before execution.

Read-only sparse overlay investigation made no edits. At32768nodes and three snapshots,256 reserved paint rows per snapshot plus4-byte node->row lookup estimates31.6MiB backing saved. Reserve on exclusive publisher capture after animation descriptions are captured; never grow during compositor Tick. Preserve ancestor dirty propagation, multiple channels/node, epoch rollover, recapture, canceled/parked tracks and generations. Existing CompositorAnimationChecks cover isolation, allocation-free ticks, retargeting, cancellation, visibility and multi-axis composition; exact capacity bounds/new tests remain undesigned. This remains a proposal, not implemented savings.

All three agents acknowledged pause and stopped. No agent builds/tests/launches were run.

## Resume sequence

1. Fix known test subscription compile error; review query concurrency patch and finish/verify tour timeout handling.
2. Run full app tests and Debug/Release builds serially; latest lyrics/masking/query changes are unverified. Engine production hasn't changed since1396checks passed; rerun applicable engine gates after engine edits.
3. Republish native ARM64 AOT and collect a complete baseline in a coordinated foreground slot. Report failures objectively before targeted performance edits. Stop measurement at failed startup readiness with preserved evidence.
4. Proceed through measured frame/cold-path/memory fixes, one causal slice at a time; update implementation plan with actual patch and evidence. Memory and cold reveals are mandatory, not deferred polish.
5. Complete full-content readiness and UI-input/presentation coverage. Native WaitForPacedWork dispatch and DirectManipulation occur partly outside FrameMs. Modal resize can nest frames while DispatchMessage waits; don't count native wait as giant CPU frame or double-count nested paint.
6. Final acceptance needs three fresh native process repetitions, all routes both laps, startup, controlled cold-data case, realistic synthetic wheel plus real touchpad and regression checks. Known violation is FAIL; missing coverage INCOMPLETE. Goal remains unfinished until actual gates and visible behavior pass.

Pause was user-directed, not completion or a technical impasse. Do not automatically resume from this handoff without a user request.
