# Native memory-floor and atlas-growth verification

Date: 2026-09-12. Implementation and verification remain in progress.

## Atlas growth: first-frame visual evidence

Both the Release JIT and published ARM64 NativeAOT probe passed on the Qualcomm Adreno X1-85.
The window rendered at 1500×960 physical pixels, scale 1.5. The first forced growth frame moved the atlas
from 2048 to 4096, epoch 0 to 1, with 1655 occupied rows, no pending text debt and no dropped instances.
It was byte-identical to the 2048 baseline and to fresh shaping against the grown atlas. The scene contains
Latin, Chinese, Arabic and Greek text, a lyric-gradient wipe and an icon; all four subpixel phases remain.
The rendered reference was also visually inspected. This is one controlled growth case, not complete
font/catalog/DPI/long-navigation coverage.

Receipts and PNGs:

- `.tmp-msbuild/glyph-capacity-first.stderr.log` and `glyph-capacity-first/`: JIT device check.
- `.tmp-msbuild/glyph-capacity-native.stderr.log` and `glyph-capacity-native/`: NativeAOT check.
- `.tmp-msbuild/memory-floor-native-probes-publish.log`: successful ARM64 native publish.

## Driver memory: three repetitions of four control arms

The native renderer-only probe uses the selected Adreno adapter, a composited 1920×1200 physical-pixel
window at scale 1.5, three swapchain buffers and maximum frame latency one. It submits 1000 paced clear
frames, primitive and text workloads, returns to minimal rendering, waits without submitting, applies its
explicit diagnostic intervention, rewarms the same text workload, then disposes swapchain and device separately.
There is no Wavee scene, artwork, audio or network load. Native D3D command volume is not yet instrumented;
portable opcode counts are labeled as such.

First baseline sample values, MiB (different memory views, **not additive**):

| Stage | Working set | Private commit | DXGI local usage | Tracked resources |
|---|---:|---:|---:|---:|
| Native device, before queue | 36.31 | 23.37 | 0.78 | 0 |
| Queue, before ring/list/fence | 36.47 | 55.52 | 0.81 | 0 |
| After 1000 clear frames, retired | 67.78 | 87.00 | 47.64 | 44.25 |
| Text workload, retired | 101.76 | 87.71 | 79.66 | 44.25 |
| Blank frame plus five-second idle | 101.68 | 87.65 | 79.66 | 44.25 |
| Swapchain disposed plus idle | 101.74 | 87.64 | 52.28 | 17.88 |
| Device disposed plus idle | 89.14 | 67.82 | 0 | 0 |

The observer retains one adapter reference through device disposal. DXGI usage is sampled freshly even
during idle. Process working set includes shared pages; private commit is not live managed object size.
No forced GC or working-set trimming is used.

All twelve NativeAOT runs completed: three each of baseline, allocator replacement, command-list reset control,
and command-list replacement. Every run recorded 1722 submits/presents at the same dimensions and DPI. Fresh DXGI
local usage was exactly 83,525,632 bytes (79.66 MiB) before intervention, after intervention plus idle, and after
replaying the same text workload. Thus every intervention's measured DXGI delta was **zero**, on all repetitions.
Allocator replacement increased private commit by 241,664–339,968 bytes; command-list replacement changed it by
−90,112 to 0 bytes. These tiny process differences do not constitute a useful memory-floor reduction.
This does **not** support adding production allocator/list trimming. These small workloads do not yet explain
the earlier full-Wavee 176 MiB retained allocation cutset beneath ExecuteCommandLists.

Raw receipts: `.tmp-msbuild/driver-floor-{baseline,allocators,reset-list,list}-{1,2,3}.stderr.log`.
Each completed probe reports PASS only for completing stages, never for meeting a memory-saving threshold.
`.tmp-msbuild/summarize-driver-floor.ps1` validates native architecture, completion, dimensions and query validity
and extracts the independent before/after/rewarm measurements. The matrix wrapper initially failed to retain a
terminated process's exit-code handle after baseline repetition 2; its completed native receipt was preserved,
the wrapper corrected, and remaining arms resumed without repeating or overwriting that measurement.

## Small-image placement: native capability and pixel proof

Both Release JIT and ARM64 NativeAOT passed the isolated small-texture probe on the Adreno adapter.
The device reports 20,480 bytes at 4,096-byte resource alignment for a 64×64 BGRA8 UNKNOWN-layout texture,
not the nominal 16,384 pixel bytes. Four disjoint resources use a 131,072-byte heap with 49,152 bytes slack,
compared with 262,144 bytes of queried allocation requirements for four standalone committed textures.
This proof therefore halves allocation requirements for its four-image arrangement; it is not a measured
whole-process reduction and does not establish production pool occupancy.

Opaque Map/WriteToSubresource/Unmap and CPU readback matched exactly; GPU-sampled pixels matched committed
references. Updating one retired texture changed exactly its 4096 pixels, leaving neighboring images and
background unchanged. The updated native PNG was visually inspected. Receipts are
`.tmp-msbuild/small-texture-placement-{first,native}.stderr.log` with four PNGs in each corresponding directory.
Production pooling and long-lived reuse were not verified by this isolated probe.

### D1 production pool: Release JIT and native ARM64 proof

The production `ImageTextureStore` path passed `--small-image-pool` on Adreno: asynchronous page activation,
first-upload committed fallback, exact original/replacement pixels, neighbor integrity, fresh SRV publication,
generation reuse, and 32 warm leases with zero additional resource/heap creations. An injected safe CPU-write
refusal disabled the placed bucket and retried committed storage with exact resulting pixels; it did not blank
the prior image. Actual device removal is not covered by that injection.

Independent 128 px query reports 69,632 bytes at 4096 alignment versus 131,072 default allocation bytes.
The receipt reports 262,144 heap bytes, 81,920 occupied required bytes, seven resource creations and zero warm
creations; heap capacity and occupancy are not additive. Pixel PNG was visually inspected. Receipt:
`.tmp-msbuild/small-image-pool-first.stderr.log`. The same production probe passed ARM64 NativeAOT with matching
counters and pixel/fallback assertions: `.tmp-msbuild/small-image-pool-native.stderr.log`.
Actual Wavee pool savings and long-session reuse remain pending.

The probe follows Microsoft's [placed-resource activation](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12device-createplacedresource)
and [CPU texture-write](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12resource-writetosubresource)
contracts. Activation is fenced before population; resources never overlap and CPU updates wait for GPU readers.

## Other verification this batch

### Preliminary native Wavee comparison (not acceptance)

The B/C1/C2 candidate was published as Release ARM64 NativeAOT with symbols, SHA256
`EBD388A45937694B33A7EFC2F332B84B4E127BAC2E90F725B432E09D75CD9974`.
The preserved baseline SHA256 is
`0361F6D679F045AD71FCC05D10E0E0D06B3C0070D4DC259C2F8C0EB42F834061`.
Both ran from the same publish directory with the same bundled assets; the baseline used a distinct executable
filename. Wavee.exe IFEO TracingFlags was verified zero (no GlobalFlag/PageHeap/Debugger value).

First matched pair uses **30 seconds warmup plus 30 seconds measurement per route**, home then PURE album.
This is preliminary, shorter than the plan's required 60/60 three-pair acceptance. Playback was paused with the
lyrics sidebar visible. Screenshots confirm the populated album, same window layout, and readable text/art.

| Route | Baseline WS MiB | Candidate WS MiB | Baseline CPU % | Candidate CPU % | Baseline GPU % | Candidate GPU % |
|---|---:|---:|---:|---:|---:|---:|
| Home | 432.46 | 435.84 | 0.103 | 0.197 | 3.496 | 3.350 |
| PURE album | 443.30 | 447.48 | 0.0214 | 0.0171 | 0 | 0 |

**No overall working-set reduction is established.** The home scene had the same 2905 live nodes, 381 components,
541 bindings, and 128 ready images in both runs. Candidate managed committed memory was about 86 MiB versus
117 MiB baseline, and LOH about 42 MiB versus 69 MiB, but other process memory offset that reduction. Glyph texture
is 4 MiB versus 16 MiB. GPU tracked totals are not directly comparable because A2 corrected allocation accounting;
DXGI usage was approximately 189.5 versus 187.0 MiB on home and 199.1 versus 196.6 MiB on album.

Receipts: `.tmp-msbuild/memory-floor-matched-{baseline,candidate}-1.result.log`, candidate memory map
`.tmp-msbuild/memory-floor-matched-candidate-1.mmp`, and candidate page-class census `.regions.json`.
The map reports approximately 324.45 MiB private-data resident, 19.87 MiB heap resident, 1.08 MiB stack resident,
and 98.25 MiB image resident (mostly shareable). These VMMap classes overlap the managed/GPU owner domains;
do not add owner counts to these totals. Native allocation offsets and repeat-run variability remain to investigate.

- Engine.Tests: D1/E1 Release 405/405; earlier pre-C2 Debug 386/386.
- Windows.Tests: D1/E1 Release 235/235.
- Engine solution D1/E1 Release build: zero errors, 186 warnings; canon passed (33 docs).
- VerticalSlice: D1/E1 Release 1536 checks passed, process exit 0 confirmed; earlier Debug also reported 1536.
- Wavee solution Release: build passed, zero errors and 69 warnings. Wavee.Tests Release: 8045 passed, one skipped, zero failed.
- Release-tool Pester: 330 passed, zero failed, one skipped.
- Final Debug verification, native Wavee A/B, production image pooling and long-load acceptance remain open.

## Factorial driver probe at 1770×1140 (2026-09-12 evening)

Thirty-nine native ARM64 runs (six families × two routes × three repetitions, plus the classic control) with zero
spread in the DXGI deltas. Full tables, readings and the two defects they exposed (the 56 MiB edge-fade strip scratch,
fixed to 28 MiB and verified on the republished probe; the driver-internal 16 MiB that appears only on steady partial
frames with two nested opacity groups, diagnosed to frame 3-10 and not yet fixed) are in
`handoff-20260912d-tooltip-edgefade.md`. Receipts: `.tmp-msbuild/factorial-pass1/` (before) and
`.tmp-msbuild/driver-floor-factorial-{edgefade,opacity}-*` (after).
