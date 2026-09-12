# Wavee memory-floor / idle-GPU handoff — 2026-09-12c

## Read this first

This is a continuation of `handoff-20260912b-idle-gpu.md`. Everything in both repositories is intentionally
**uncommitted** and belongs to the user. Run `git status --short` in `C:\wavee\waveemusic` and
`C:\wavee\fluent-gpu` before touching anything. Do not reset, stash, checkout, commit, or redo the earlier work.
Read `C:\wavee\waveemusic\CLAUDE.md`, `C:\wavee\waveemusic\AGENTS.md`, the sibling engine rules, and the
`fluentgpu` skill before editing. Never round-trip source through PowerShell 5.1 `Get-Content`/`Set-Content`;
use UTF-8 and run `git diff --numstat` after every scripted edit.

The objective remains: continue until CPU, GPU, memory, playback/lyrics sync, scrolling, image stability, and the
remaining orphan/maintenance behavior are fixed and verified on the real ARM64 Wavee build. Do not call this done
from headless tests or owner counters alone.

## Current live process

The latest D1/E1 NativeAOT candidate is running:

```
PID 10144
exe C:\wavee\waveemusic\src\apps\Wavee\bin\publish-aot-symbols\Wavee.exe
started 2026-09-12 16:04:01
```

It may be playing `spotify:track:3ZFwuJwUpIl0GeXsvF1ELf` (Nothing Else Matters). Close it gracefully before any
publish that writes `publish-aot-symbols`; validate the executable path first and do not kill it unless it is hung.
Current observed process size at handoff was WS 569.9 MiB / private 499.2 MiB during later playback/load;
this is not a steady-state acceptance number.

## What is implemented and verified

In `C:\wavee\fluent-gpu`:

- Idle GPU fix: σ>0 blur veto removed for safe clamped replay; Acrylic and stencil remain vetoed. Prior real AOT
  settled idle measurement reached 0% GPU and approximately 0.009% CPU; minimized GPU was 0%.
- C1 sparse text-style snapshots, B transactional 2048→4096 glyph growth and free-quad pooling, A2 truthful
  D3D12 allocation accounting, and C2 reachability-sized snapshots/cold reclamation are present.
- D1 production small-image placement pool is present: queried sizes, 128 KiB first page / 1 MiB growth pages,
  16 MiB aggregate cap, asynchronous fenced activation, immutable leases/generation checks, warm reuse, and
  committed fallback on healthy write refusal. GPU-produced/adopted baked images do not enter the CPU pool.
- E1 UI cold maintenance is present: one-shot 30-second deadlines, race-safe pixel-pool rearming, scene-capacity
  revision, C2 Free-slot deadline, minimized-safe wake, and no maintenance GPU frame. Render-owned maintenance,
  fence-only retirement separation, and wall-clock RT target retirement remain open.
- A targeted driver-memory factorial probe was started by the prior agent in
  `src/FluentGpu.WindowsApp/Probes/DriverMemoryFloorProbe.Factorial.cs`; it is not yet fully verified.

Verified after D1/E1 source changes:

```
Engine solution Debug/Release build: 0 errors (warning backlog exists)
FluentGpu.Engine.Tests: 405/405 Debug and Release
FluentGpu.Windows.Tests: 235/235 Debug and Release
VerticalSlice: 1536/1536 Debug and Release, explicit exit 0
Wavee solution: Debug/Release build, 0 errors
Wavee.Tests: 8045 passed, 1 skipped, 0 failed, Debug and Release
Design canon: 33 documents, clean
Production small-image probe: JIT and ARM64 NativeAOT pass
```

The production pool probe receipt reports `required64=20480`, `required128=69632`, `alignment=4096`, heap bytes
262144, occupied required bytes 81920, 7 resource creates, and 0 warm creates. It proves correctness and reuse,
not whole-process savings. Receipts are under `.tmp-msbuild\small-image-pool-{first,native}.stderr.log`.

## Real Wavee measurements already taken

The D1/E1 candidate was published with SHA256
`AE56A472178665137B2F531E4B8261A2B3D3CF52DBB39BB8880C76317A57CC9C`.
The preserved old baseline is `0361F6D679F045AD71FCC05D10E0E0D06B3C0070D4DC259C2F8C0EB42F834061`.

- Candidate settled PURE album: 0% GPU, approximately 0.004% CPU, about 449 MiB WS in that launch. Thumbnail
  pool was active (`Image.PlacedHeap`) and the pixel pool drained to 0 after settling.
- Candidate playback with lyrics visible: approximately 1.21% CPU and 6.62% GPU over 30 seconds; synchronized
  syllable/line visuals were visible in the screenshot.
- Candidate playback with lyrics closed: approximately 0.31% CPU and 0.41% GPU over 30 seconds.
- Candidate minimized during playback: 0% GPU and approximately 0.09% CPU.
- Candidate scrolling: 256 synthetic mouse-wheel events over 12.0 seconds; Wavee recorded 1180 scroll frames,
  average frame 0.4 ms, no over-budget frames, 0 text misses, and no audio-underrun log was observed. The helper
  uses foreground `mouse_event`; the older `WM_MOUSEWHEEL` post-message helper is not valid proof of scrolling.
- Artwork watch during playback: 225 samples over 12 seconds, 0 changes after the initial sample; image remained
  stable and the screenshot showed the correct track art.
- Thread CPU sample during no-lyrics playback: `fgpu-ui` ~11.97 ms/s, `fgpu-render` ~7.81 ms/s,
  `FluentGpu.AudioProducer` ~6.77 ms/s, `FluentGpu.AudioRT` ~1.04 ms/s. This is boundary sampling, not a stack
  profile and excludes threads that start/exit inside the interval.

## Why memory is not yet fixed

Matched 30-second warmup + 30-second measurement runs were too noisy for a whole-process claim:

```
old baseline home WS 432.46 MiB; candidate home 435.84 MiB
old baseline album WS 443.30 MiB (run 1), 460.88 MiB (run 2); candidate album 447.48 MiB
```

Managed heap/LOH and tracked GPU owners improved in the candidate, but private/native memory offset the savings.
VMMap candidate page classes showed approximately 324.45 MiB private-data resident, 19.87 MiB private heap,
1.08 MiB stacks, and 98.25 MiB image resident; these classes overlap app counters and must not be added.

Strong same-PID evidence shows driver/untracked memory rising in roughly 32/64 MiB steps after navigation and
remaining after temporary render targets retire. The old standalone driver probe was not representative: it forced
FullDirect, had no images/layers/blur/edge-fade, and used a different physical target. The factorial probe should
compare realistic retained/partial versus direct workloads at approximately 1770×1140 physical pixels, with images,
opacity, blur, edge fades, canvas, and stencil/layer families isolated.

## Concrete remaining work (in order)

1. Poll/finish the current native app test logs and close PID 10144 gracefully when the live measurements are saved.
2. Run the factorial driver probe in fresh processes; retain the previous controls and report actual dimensions,
   route, submit/present counts, DXGI usage, tracked/untracked bytes, and cold-stage deltas. Do not infer ownership
   from one process or add overlapping counters.
3. Replace `ToolTipClock`'s fake `FrameClock.Tick` + invisible `Opacity 1→1` animation with a one-shot `UseTimeout`
   deadline/due signal. Preserve the visible tooltip fades, show delay (800 ms), open dwell (5 s), safe-zone check
   (1 s), cancellation semantics, and CommandBarFlyout completion timing. Required gate: no frame-clock subscriber
   and no per-frame component renders while a tooltip is hidden/settling. The measured scroll run showed roughly
   593 unnecessary tooltip renders in 12 seconds and `ToolTipClock` was the sole poller.
4. Build/test the tooltip change and republish Wavee. Repeat playback with lyrics open/closed, scroll, artwork watch,
   minimize/restore, and idle samples. Use 60-second warmup/measurement and three alternating matched runs before
   claiming a process-memory change.
5. Implement only the remaining render-owned E1 maintenance if the factorial evidence warrants it: separate
   completed-fence retirement from upload recording, use an event/deadline without dummy submits or present credit,
   preserve child-drain acknowledgements, and add no recurring idle poll.
6. Investigate the old orphan mechanism only with a real render-owned reproducer that remounts a keyed Enter/Exit
   pair every second. Explain why `animTracks=4` persisted when only the exit pair was on the orphan before changing
   the mask; headless `gate.anim.flip-cell-idles` is insufficient evidence.
7. Continue the required long-load acceptance: playing track, syllable lyrics, scrolling/navigation, art stability,
   minimized/restore, at least 30 minutes and 100 route cycles. Record CPU-ms/s, allocations/s, WS/private/GC,
   owner bytes, frame p95/p99, presents, misses, underruns, and restore latency.

## Important files and receipts

- Plan: `docs/plans/wavee/astra-lower-memory-floor-20260912-implementation.md`
- Native results: `docs/plans/wavee/memory-floor-native-probes-20260912.md`
- Original idle investigation: `docs/plans/wavee/handoff-20260912b-idle-gpu.md`
- App helper: `.tmp-msbuild/idle-gpu-live.ps1`
- Thread sample helper: `.tmp-msbuild/sample-wavee-threads.ps1`
- Candidate log: `%LOCALAPPDATA%\Wavee\logs\wavee-20260912.log` (filter `pid=10144` and session id)
- Candidate test receipts: `.tmp-msbuild\memory-floor-d1-e1-*.log`
- Matched baseline/candidate receipts: `.tmp-msbuild\memory-floor-matched-*.result.log`
- VMMap: `.tmp-msbuild\memory-floor-matched-candidate-1.mmp` and `.regions.json`

## Safe handoff rules

Only the orchestrator builds, tests, publishes, launches, and monitors. Keep source edits disjoint. After each edit,
run `git diff --numstat` (and for an untracked file `git diff --no-index --numstat -- NUL <file>`, accepting exit 1).
Do not overwrite the preserved baseline executable/PDB. Do not treat a lower owner counter as a process-memory win.
Do not use environment-variable behavior switches. Keep all diagnostics and dumps local.
