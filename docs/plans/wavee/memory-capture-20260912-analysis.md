# Wavee memory capture — 2026-09-12

Status: capture and memory root-cause analysis complete for the observed sessions. This is not a claim that the optimization work is complete or that an unbounded leak has been established.

## Authoritative capture

Local artifacts are in `.tmp-msbuild/memory-focused-20260912/`:

- `wavee-navigation.etl`: 1,790,967,808 bytes, focused Win32 heap and virtual-allocation tracing.
- `recorder.log`: recording saved successfully, heap tracing disabled afterward, no dropped-event warning.
- `finished.txt`: recorder cleanup completed.
- `phases.log`: launch PID 29440 at 14:19:14, resume and Liked Songs at 14:19:27, PURE album at 14:19:33, Home at 14:19:58 (local time).
- `album.mmp`: VMMap snapshot from the instrumented process.
- `virtual-analysis.wpaProfile`: virtual-allocation stack export configuration; the completed CSV's retained rows were inspected and reconciled below.

WPA Exporter loaded this trace without `-tle` (the option that permits lost events). The earlier 6,093,275,136-byte combined CPU/heap/virtual trace in `memory-symbolized-20260912` reported **82,837 lost events** and is not an exact allocation-balance source.

Tooling caveat: the installed WPA exporter emits a missing `wpaexporter.deps.json` startup warning, surfaced by PowerShell as `NativeCommandError`; the wrapper reports exit 1. Nevertheless it processes the trace and writes the complete, symbolized CSV. Its log contains no lost-event or export-failure diagnostic. Validation here relies on the recorder's successful completion, actual exported rows, and independent accounting checks, not a claimed clean exporter exit code.

## Binary identity

Release NativeAOT ARM64, built from the existing dirty worktree with `ops/build/publish-wavee-aot.ps1 -Arch arm64 -Symbols`.

Output: `src/apps/Wavee/bin/publish-aot-symbols/`. Executable 45,200,896 bytes; PDB 181,932,032 bytes, both generated at 14:08:35. PE CodeView RSDS and PDB MSF stream 1 were independently read: both have GUID `eeba7773-1354-49f1-a69f-bbd4b5371eb6`, age **51**. The previous normal executable had no RSDS entry; an old adjacent PDB was not accepted as matching.

## VMMap accounting

Top-level regions only, avoiding double-counting nested regions. Resident = PrivateWS + ShareableWS. Snapshot is instrumented, **not normal-run baseline memory**.

| Category | Resident MiB | Committed MiB |
|---|---:|---:|
| Private Data | 377.445 | 406.039 |
| Image (ASLR) | 93.918 | 365.824 |
| Heap (Private Data) | 22.129 | 23.625 |
| Mapped File | 1.859 | 71.891 |
| Thread Stack | 1.348 | 1.773 |
| Shareable | 1.344 | 5.480 |
| Heap (Shareable) | 0.004 | 0.063 |

Image and mapped-file committed totals are not private commit. Neither DXGI usage nor GC committed bytes may simply be added to or subtracted from working set as independent physical allocations. Heap tracing changes the process footprint.

## Navigation evidence and limits

The application log confirms PID 29440 route changes and a PURE album render with **13 BoundRowContent instances**. Its album nav-end sample reports heap 116.0 MiB, GC committed 130.7 MiB, fragmentation 8.0 MiB, tracked GPU resources 101.6 MiB, 210 ready images, zero orphans and zero animation tracks. These are overlapping diagnostics, not extra VMMap categories.

The preceding PID 28384 combined trace also logged a LikedArtistsCard mount allocating approximately 8,062 KiB and taking 168 ms under instrumentation. That identifies allocation churn, not retained-memory ownership, and is not an uninstrumented performance benchmark.

Scrolling is **not yet verified**. Foreground-based input aborted when another app took focus. Targeted legacy wheel messages were delivered but FluentGpu uses pointer-wheel input; Windows rejected targeted pointer-wheel injection. Neither attempt is counted as a successful scrolling test. Route navigation and playback are verified independently.

After recording, PID 29440 was closed gracefully and the same symbol-enabled executable restarted without heap tracing as PID 11560. No production edits were made for this capture. Existing uncommitted optimizations remain preserved and have their own outstanding verification requirements.

## Allocation-stack result

`virtualVirtualAlloc_Commit_LifeTimes_Wavee_commit_stacks.csv` contains 76,591 exported rows. Select **AIFO** (allocated inside the selection and freed outside it) for retained allocations. The retained root is **483.492 MiB**. Do not sum inclusive stack rows: their parents and children describe the same allocations.

| Retained virtual-allocation category | MiB |
|---|---:|
| Graphics allocation backing (`dxgkrnl` / VidMm paths) | 304.109 |
| NativeAOT GC (`gc_heap` / `GCToOSInterface`) | 151.116 |
| Windows heap backing | 23.482 |
| Other | 4.785 |
| **Total** | **483.492** |

Validation: graphics and GC totals were independently recomputed by summing only each stack's **first entry** into its category, excluding descendants. These independently reproduce **304.109** and **151.116**. The full category balance reproduces the root to export precision. The CSV includes collapsed identical textual paths and repeated terminal rows; blindly deduplicating rows or discarding negative parent/child cancellation gives incorrect totals. `analyze-virtual-stacks.ps1`, `virtual-summary.json`, and `virtual-owners.csv` preserve the calculation. The earlier `retained-exclusive.csv` is an exploratory, invalid intermediate and must not be used.

### Largest graphics causes

- **176.094 MiB** enters through `D3D12Core!CCommandQueue::ExecuteCommandLists`, independently summed at the first command-submission frame. Deeper stacks pass through the Qualcomm D3D12 driver, `NDXGI::CDevice::AllocateCB_0022`, and VidMm backing-store commitment. This is real graphics backing committed in response to submission, not album metadata or PCM samples. The driver's private PDB is unavailable, so the precise internal buffer purpose is not established.
- Approximately **37 MiB** enters via `ImageTextureStore.CreateTexture`.
- Approximately **33.087 MiB** enters through device initialization; **16.004 MiB** through the GPU glyph atlas; **8.008 MiB** through the stencil target; **7.938 MiB** through acrylic targets. These are subcategories of graphics backing, not additions to it.
- Code inspection confirms the engine creates a bounded command-allocator ring and resets its current allocator after waiting for its fence (`D3D12Device.cs` near 1312 and 1655). This does **not** support a claim that a fresh command allocator leaks every frame. Allocator/driver high-water retention needs an A/B experiment before assigning blame to a specific retention mechanism.

### GC causes and limits

GC segment commitment occurs beneath image decoding/cache reads, `SceneRecordingSnapshot.EnsureCapacity`, `SceneStore.ResizeColumns`, metadata/cache hydration, and the CPU glyph atlas allocation. The glyph constructor alone triggers a **16 MiB** commitment, consistent with the earlier managed snapshot's 16,777,240-byte array (including object overhead). The CPU and GPU glyph atlases are separate allocations.

A segment's triggering allocation stack is **not** proof that every surviving object in that segment belongs to that caller. NativeAOT virtual-allocation tracing supplies segment ownership, not a managed retained-object graph. The earlier JIT GC snapshot is useful corroboration, not an exact object census of this native process. No such exact native retained-object census is claimed.

## Additional user-navigation capture

At the user's request, PID **11560** was captured without restarting it after further manual scrolling/navigation. Artifacts in `.tmp-msbuild/memory-user-navigation-20260912/`:

- `wavee-after-navigation.dmp`: **1,072,237,864 bytes**, a full cloned-process dump produced by Microsoft-signed ARM64 ProcDump. ProcDump reported completion in 2.2 seconds; the file has an `MDMP` header. It is local and may contain session/account data; do not upload it.
- `after-user-navigation.mmp`: valid VMMap snapshot.
- `memory-regions.json`: independent VirtualQuery / working-set census.
- `procdump.log`: collection result.

The pre-capture process read was 622,518,272 resident bytes and 559,419,392 private committed bytes. The later VMMap snapshot reports **470.371 MiB private data**, **104.441 MiB image pages**, **26.941 MiB Windows heap**, and about **5.3 MiB** in other resident categories (approximately **607.05 MiB** total). These are different instants; they must not be presented as simultaneous exact counters.

A subsequent same-PID periodic sample shows GC committed **182.2 MiB**, heap **171.5 MiB** including **6.0 MiB** fragmentation, **1,732 image entries / 524 ready images**, **40.0 MiB image bytes**, a **16 MiB pixel pool**, and **103.7 MiB tracked GPU resources**. It still reports **zero orphans and zero animation tracks**. This supports image/cache and GC retention after navigation, not a recurrence of the orphan pin. It does not establish growth without bound. Manual scrolling happened in this later session, not in the earlier ETL interval.

## Root cause and optimization priorities

The several-hundred-MiB footprint is not principally the music audio buffer. The observed architecture combines large graphics-backed commitments (including substantial driver-side allocations made during command submission), a sizeable NativeAOT GC heap containing renderer arrays and application/image caches, and roughly 94–104 MiB of resident executable/DLL pages. The measurements explain why **under 100 MiB is not achievable with this current resource configuration**; that target requires architectural/resource-budget changes, not SIMD alone.

1. **Engine graphics first:** isolate command-submission backing with a controlled minimal-scene versus full-scene A/B capture; examine command volume, allocator high-water behavior, and driver allocation lifetime. Do not simply reduce frames-in-flight or recreate allocators without validating fence safety and latency.
2. **Explicit renderer resources:** budget/reclaim render-target pools; evaluate smaller/on-demand CPU and GPU glyph atlases and image texture packing. Keep upload lifetime and image-transition correctness covered.
3. **GC and caches:** enforce aggregate image/decode/pixel budgets and reclaim stale decoded data; reduce scene/snapshot capacity duplication; inspect retained metadata/image cache roots in a managed diagnostic build. Distinguish allocation churn from retained capacity.
4. **Code residency:** audit eagerly initialized platform/module paths only after larger graphics and GC causes; do not equate mapped image size with resident/private memory.

Limits: no claim of an unbounded leak, no precise names for proprietary driver buffers, no complete NativeAOT managed object graph, no extrapolation of instrumented timings to normal CPU/GPU utilization, and no completed optimization/fix claim. Those require separate experiments. Recording and exporter processes have exited, heap tracing has been disabled, and Wavee remains running normally.
