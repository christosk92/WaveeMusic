# Wavee performance measurement plan

This is the publication protocol for comparing the released WinUI 3 WaveeMusic app with the new FluentGPU app. It exists to produce numbers that are repeatable, understandable, and safe to quote in the README or a release announcement.

## What is already publishable

| Result | Status | Scope |
|---|---|---|
| 7.7 ms → 1.74 ms presented sample-time spread | Verified | Three paired synthetic scroll runs on a 120 Hz panel |
| ~25% → ~93% of frames within 1 ms of the timing mode | Verified | Same paired scroll runs |
| 1.32 → 1.00 publishes per present | Verified | Same paired scroll runs |
| 13–16.5 ms fence stall removed from the UI loop; UI submit measured 0.0 ms | Verified | Real D3D12 async-render validation |
| 0 B managed allocation per steady paint frame | Verified | FluentGpu.VerticalSlice full-suite invariant; not a whole-app claim |
| 97 resize/scroll rounds with zero device loss | Verified | Four-minute real-window validation run |
| 332 ms p50 / 367 ms p95 process-to-window readiness | Verified | 19 warm-cache launches of the current win-arm64 NativeAOT Wavee binary |
| Under 0.01% total 12-core capacity at loaded idle | Verified | 300 one-second per-process samples after a 30-second warm-up |
| 1.55 KiB/s median instrumented whole-process idle allocation | Verified with overhead caveat | Four post-startup census windows, 0.8–1.6 KiB/s; reporter overhead is included |
| −0.2 MiB working-set change, first versus last 30-second average | Verified | Same five-minute fake-Home idle run |
| 28.9 MiB unpackaged NativeAOT executable | Verified | Exact on-disk file length; not an MSIX measurement |

The optimization chart input and source commits are in [`benchmark-data/fluentgpu-progress.json`](benchmark-data/fluentgpu-progress.json). The current-binary snapshot, machine metadata, binary hash, limitations, and rejected captures are in [`benchmark-data/fluentgpu-binary-2026-07-26.json`](benchmark-data/fluentgpu-binary-2026-07-26.json).

## What is not publishable yet

The current `WaveePerfBench` output is useful for diagnosis but not for a public WinUI 3-versus-FluentGPU comparison:

- its active CPU scenarios complete in roughly 0.02–0.15 seconds, below a useful process-CPU sampling window;
- static loops can append the same `LastStats` value repeatedly when no new frame is painted;
- all scenarios run in one process, so later memory peaks contain caches and retained state from earlier scenarios;
- the July 26 exploratory run logged a failed fake-image decode;
- it was a single FluentGPU run with no paired WinUI 3 package;
- another Wavee process was active on the machine during capture.

None of its CPU, frame-percentile, or memory values should appear in promotional material.

The later current-binary run replaced those generic-bench values with external PID-bound sampling: 20 launches, a 30-second warm-up, 300 one-second idle samples, and a separate GPU-aware census. It is publishable as a scoped developer-machine snapshot, but not as a WinUI comparison.

## Controlled test environment

Every published comparison records:

- app name, semantic version, commit, package identity, architecture, configuration, and SHA-256;
- machine model, CPU, GPU, RAM, Windows build, .NET runtime, and graphics-driver version;
- display resolution, scaling, refresh rate, VRR state, and HDR state;
- AC/battery state and Windows power mode;
- whether the run is cold or warm, cache state, account, and fixture version;
- probe configuration and raw-artifact paths.

Before a run:

1. Use signed Release/NativeAOT packages of the same architecture.
2. Reboot or apply the campaign's documented reset procedure.
3. Plug into AC, select the same power mode, and wait five minutes for temperature and background activity to settle.
4. Close every existing Wavee instance and pause unrelated builds, sync clients, updates, and profiling tools.
5. Verify the target display is running at 120 Hz.
6. Alternate package order (`WinUI, FluentGPU, FluentGPU, WinUI`) to reduce thermal and time-order bias.

## Campaign A — startup

Measure 20 cold launches and 20 warm launches per package.

Endpoints:

- process creation → first top-level window;
- process creation → first successful present;
- process creation → interactive shell marker;
- process creation → first populated Home frame.

Report p50, p95, minimum, maximum, and all sample counts. Cold runs use a terminated process and the documented cache reset; warm runs retain application caches but still start a new process.

Instrumentation still needed:

- matching startup `EventSource` markers in both apps;
- a FluentGPU first-present/interactive marker;
- an external launcher that records process creation and packages each run's metadata.

## Campaign B — navigation

Use a fixed fixture containing the same representative artist, album, 100-track playlist, and 10,000-track library in both apps.

For each route, run 30 cold and 30 warm navigations:

- Home → artist;
- Home → album;
- Home → playlist;
- Home → Liked Songs;
- back to the previously visited page.

Endpoints:

- input/navigation request → first frame showing destination content;
- request → stable content frame;
- request → interactive destination.

Report p50/p95/p99 latency, frames above 8.33 ms and 16.67 ms, allocation per navigation, and collections during the navigation. Use the existing WinUI `Wavee-UI-Navigation` ETW provider and FluentGPU navigation/frame probes, joined to successful presents.

## Campaign C — 120 Hz scroll quality

Run five 30-second captures for each fixed surface:

- Home shelves;
- Liked Songs;
- the 100-track playlist;
- the 10,000-track library.

Drive the same wheel or precision-touchpad trace in both packages. Collect cadence with PresentMon plus app timestamps.

Report:

- successful presents and present interval p50/p95/p99;
- missed-vsync count and rate;
- frames above 8.33 ms, 12.5 ms, and 16.67 ms;
- sample-time spread and frames within 1 ms of the timing mode;
- input → acknowledged present p50/p95 when hardware-grade timestamps are available.

Do not enable allocation, type-allocation, memory-census, screenshot, or verbose logging probes during cadence capture.

## Campaign D — memory, allocation, and GC

This is a separate run from Campaign C. Run the same ten-minute navigation/scroll script three times per package.

Capture once per second:

- working set and private bytes;
- managed heap, committed heap, and fragmentation;
- total allocated bytes and allocation rate;
- Gen 0/1/2 counts;
- total and maximum GC pause;
- image/cache counts where both apps expose a comparable value.

Record settled points after login, after Home, at peak workload, after returning Home, and after a documented full-GC diagnostic sample. Report median and p95 allocation rate, peak memory, settled memory, total collections, maximum pause, and retained-memory slope.

On UMA/integrated-GPU machines, process working set is a combined residency signal. It can include managed pages, native heaps, mapped upload/readback resources, shared graphics allocations, driver residency, and decoded surfaces. Always report the engine's tracked live D3D12 resources and managed heap alongside working set; do not subtract or add these overlapping views as though they were disjoint buckets.

Also run the FluentGPU distinct-entity memory soak for at least 1,000 navigations and fit managed-floor and working-set slopes against navigation count. The WinUI package needs an equivalent route driver before a slope comparison is published.

## Campaign E — idle cost and package footprint

For idle cost, leave the fully loaded Home page untouched for five minutes, three times per package. Report the last four minutes:

- average and p95 process CPU;
- average GPU engine use;
- working set and private bytes;
- energy use only if the same repeatable Windows energy counter is available for both packages.

For footprint, report:

- downloaded signed MSIX size;
- installed package footprint;
- unpacked application footprint;
- architecture and whether symbols/diagnostics were excluded.

Only the actual Wavee packages count. Gallery or engine-demo package sizes must not be presented as Wavee results.

## Campaign F — reliability

Run three 30-minute real-window soaks per package:

- repeated Home/album/artist/playlist/Liked navigation;
- bidirectional scroll;
- resize, maximize, restore, minimize, and display-scale transitions;
- playback/Connect smoke actions where deterministic automation is available.

Report crashes, device losses, failed navigations, failed presents, maximum frame interval, peak memory, settled-memory slope, and whether playback remained responsive.

## Publication rules

A comparison can enter the README, chart, or announcement only when:

- both packages ran the same workload on the same machine and day;
- run counts and p50/p95 values are available;
- the package/commit identity and probe configuration are recorded;
- raw artifacts exist and no run was silently discarded;
- probe failures, decode failures, background interference, or dirty builds are disclosed;
- the claim names its scope—for example, “steady paint path” rather than “the app allocates nothing.”

The first chart continues to show the verified FluentGPU engineering results. A second `WinUI 3 → FluentGPU` product chart is generated only after Campaigns A–E have paired data. Its headline cards will be startup, navigation p95, missed-vsync rate, ten-minute peak/settled memory, allocation/GC pause, and signed-MSIX footprint.

## Definition of done for the MSIX announcement

- Campaigns A–E completed with paired signed packages.
- At least one 30-minute FluentGPU reliability soak completed with no crash or device loss.
- Raw results archived with package hashes and machine metadata.
- Matplotlib chart regenerated from the curated result JSON.
- README and announcement claims cross-checked against that JSON.
- The package install/update/uninstall, authentication, playback, Connect, and recovery gates passed.
