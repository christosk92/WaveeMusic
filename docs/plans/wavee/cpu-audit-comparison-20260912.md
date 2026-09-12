# CPU and memory audit comparison — 2026-09-12

Requested parallel reviews: GPT-6-Astra **high** inspected the public code; GPT-6-Astra **medium** researched
optimization techniques using primary sources. Both reviews were read-only. Neither estimated a promised total
CPU/working-set saving. All existing changes remain uncommitted.

| Priority | Code evidence | Research comparison / decision |
|---|---|---|
| Fix artwork clock | ImageCache accumulated resynced/clamped animation delta; rendering extrapolated wall time. Sparse publication rewound fades. | Stop unnecessary frame production first; implement and verify the clock correction before interpreting playback GPU. |
| Remove redundant audio work | AudioFeedThread wakes its manager every 20 ms although live rings have dedicated producers; control maintenance runs roughly every 15 ms. RMS/peak scans run even without a meter consumer; neutral DSP stages still traverse buffers. | Event-driven work, demand-driven meters, and neutral-stage bypass precede SIMD. Existing WASAPI output is already event-driven: no blanket backend rewrite. Impact needs actual CPU profiling and underrun/seek/gapless tests. |
| Reduce retained reserves | Fixed 4096-square CPU glyph mirror is 16 MiB. Three dense snapshot text-cache arrays total about 3.09 MB in the JIT heap; UI sparse cache is about 151 KB. | Budget actual bytes. Adaptive atlas sizing and sparse snapshot caches are candidates, not implemented savings. Blindly halving the atlas can cause mixed-script/subpixel raster churn. |
| Avoid whole-scene work | Snapshot capture still walks live nodes for references during record-only lyric frames. | Retain stable layout/references and invalidate narrowly. Do not reduce syllable sync cadence to hide traversal cost. Profile CPU before changing the capture contract. |

The existing glyph-upload change is implemented and observed: three banks shrink from **12 to 3 MiB** after clean
submitted frames. That is not a claim that total process working set drops by the same amount.

## Evidence and limits

Native ARM64 run before the artwork-clock/drift fixes: audio-only, lyrics closed, 15 samples averaged **0.514% GPU,
0.324% CPU**, WS **498.9 MB**. Liked Songs with syllable lyrics, 20 samples: **7.306% GPU, 1.428% CPU**, WS **546.5 MB**.
These are workload-specific observations, not acceptable-target declarations. CPU is normalized over 12 logical
processors; 0.324% represents approximately 38.9 aggregate CPU-ms per second.

A separate JIT diagnostic heap contained **86,766,685 managed bytes / 221,845 objects**. It identified retained
types, not native-AOT total memory. Byte arrays include a 16 MiB atlas mirror and roughly 8.46 MB of 32 KB arrays
whose ownership is not yet established. A large working set alone does not prove a managed leak.

The Windows `dotnet-sampled-thread-time` trace includes sleeping threads. Its Wait stacks **must not** be reported
as CPU hotspots. Use ETW/WPA CPU sampling for on-CPU attribution. NativeAOT EventPipe support is conditional on
build/runtime support; the diagnostic JIT run is not evidence that all NativeAOT processes lack tracing.

## Primary sources from the research agent

- [Windows app power guidance](https://learn.microsoft.com/en-us/windows/apps/performance/power): eliminate avoidable timers/work; render on demand.
- [DXGI frame-latency waitable object](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_3/nf-dxgi1_3-idxgiswapchain2-getframelatencywaitableobject): pacing support, not permission to continually produce unchanged frames.
- [WPA CPU analysis](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/cpu-analysis) and [dotnet-trace](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace): distinguish on-CPU samples from thread time.
- [NativeAOT diagnostics](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/diagnostics): supported diagnostic mechanisms and build constraints.
- [Low-latency audio](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/low-latency-audio): smaller periods trade power for latency; music playback need not request minimum latency.
- [Direct2D performance guidance](https://learn.microsoft.com/en-us/windows/win32/direct2d/improving-direct2d-performance): reuse retained resources/layout; applying this to our custom renderer is an inference.
- [Working set](https://learn.microsoft.com/en-us/windows/win32/memory/working-set), [process memory counters](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex), and [D3D resource heaps](https://microsoft.github.io/DirectX-Specs/d3d/ResourceHeaps.html): separate private commit, managed heap, residency and overlapping UMA accounting.
- [SIMD](https://learn.microsoft.com/en-us/dotnet/standard/simd): a later option for measured sample-processing hotspots, not a substitute for avoiding unused passes.

No SIMD/audio-polling/atlas-capacity/sparse-snapshot rewrite has been made as part of these reviews.
