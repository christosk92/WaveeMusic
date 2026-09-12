# Copy-paste continuation prompt

You are continuing the Wavee performance work. Read
`C:\wavee\waveemusic\docs\plans\wavee\handoff-20260912c-memory-floor.md` first, then read the referenced
older handoff's Operational notes. The user wants this pursued until it is genuinely fixed.

Everything in `C:\wavee\waveemusic` and `C:\wavee\fluent-gpu` is uncommitted user work. Run `git status --short`
in both before touching anything. Do not reset, stash, checkout, commit, redo, or discard existing edits. Only the
orchestrator may build, test, publish, launch, or monitor. Use `apply_patch`; never round-trip source with PowerShell
5.1 `Get-Content`/`Set-Content`; use UTF-8 and check `git diff --numstat` after every scripted edit.

Current candidate: `C:\wavee\waveemusic\src\apps\Wavee\bin\publish-aot-symbols\Wavee.exe`, PID 10144 may still
be running. Close it gracefully before publishing. D1 production small-image pooling and E1 UI one-shot cold
maintenance are implemented. Engine tests 405/405, Windows tests 235/235, VerticalSlice 1536/1536, Wavee tests
8045 passed/1 skipped, and Debug/Release builds are green. Native JIT and ARM64 pool probes pass exact pixels,
neighbor/SRV/generation/fallback/warm-reuse checks.

Do not claim memory is fixed: matched native runs were noisy (baseline album 443/461 MiB, candidate 447 MiB).
Current priorities are:

1. Finish the realistic driver-memory factorial probe at ~1770×1140 physical pixels, isolating retained/direct,
   images, opacity, blur, edge-fade, canvas/layer/stencil workloads and reporting actual submit/present/DXGI/
   tracked-untracked deltas.
2. Replace `ToolTipClock`'s fake frame-clock subscriber and invisible animation timer with a one-shot timeout while
   preserving tooltip delay/dwell/safe-zone and CommandBarFlyout completion. The live scroll run showed ~593 useless
   tooltip renders in 12 seconds; add behavioral tests proving no frame-clock poller/per-frame rerender when hidden.
3. Rebuild all Debug/Release gates, republish ARM64, and rerun real Wavee: idle/minimized, playback, lyrics syllable
   sync, lyrics closed, scrolling, navigation, artwork stability, and long load. Use matched 60/60-second runs and
   three repetitions before attributing process memory savings.
4. Finish render-owned E1 fence-retirement maintenance and the genuine render-owned keyed Enter/Exit orphan reproducer
   only after preserving no-submit/no-dummy-frame/no-recurring-poll invariants. Explain the old four-track orphan.

Evidence already available: `.tmp-msbuild/memory-floor-d1-e1-*.log`, `.tmp-msbuild/memory-floor-matched-*.result.log`,
`.tmp-msbuild/memory-floor-matched-candidate-1.mmp`, `%LOCALAPPDATA%\Wavee\logs\wavee-20260912.log`, and the
artwork/lyrics/scroll screenshots. Keep diagnostics local and do not inspect fenced private PlayPlay paths.
