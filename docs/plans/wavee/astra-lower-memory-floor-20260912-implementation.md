# Lowering Wavee's memory floor: architectural implementation plan

Date: 2026-09-12. Author: GPT-6 Astra, high reasoning, requested by the user.
Status: implementation in progress; no native memory-saving claim yet.

Latest implementation checkpoint (2026-09-12, supersedes earlier verification below):
- B/C1/C2 are published in the first native candidate. Glyph first-growth frame passed exact pixel checks in JIT
  and ARM64 NativeAOT. C2 reachability-sized snapshots and Free-slot cold replacement preserve reader/pin ownership.
- D1 small-image placed heap pool is implemented with queried 64/128 px allocation sizes, asynchronous activation,
  bounded heap capacity, immutable published leases and safe committed fallback. Production probes passed JIT and
  ARM64 NativeAOT, including exact pixels, neighbors, generations and zero resource creations across warm leases.
- E1 UI one-shot maintenance is implemented and tested, including minimized scheduling and worker-return races.
  Render-owned no-submit retirement/RT wall-clock maintenance remains open.
- Engine solution Debug and Release builds passed; Engine.Tests 405/405, Windows.Tests 235/235 and VerticalSlice
  1536/1536 passed in both configurations, with explicit exit 0. Wavee solution builds and 8045 app tests passed in
  both configurations (one skipped). Existing warning backlogs remain; canon passed (33 documents).
- D1/E1 Wavee native publish is in progress. Actual pool savings and native maintenance/load acceptance are pending.
- Preliminary B/C1/C2 comparison does not establish a working-set reduction: baseline album runs 443/461 MiB,
  candidate 447 MiB. These were 30/30-second probes, not the required three alternating 60/60 acceptance pairs.
  See [native receipts and limitations](memory-floor-native-probes-20260912.md).
- The preserved B/C1/C2 executable/PDB is `.tmp-msbuild/memory-floor-bc-native-20260912`, SHA256
  `EBD388A45937694B33A7EFC2F332B84B4E127BAC2E90F725B432E09D75CD9974`.

Earlier implementation checkpoint (historical verification):
- Fresh pre-change Release VerticalSlice: 1,536 checks passed (`.tmp-msbuild/memory-floor-baseline-release.log`).
- C1 sparse recording text styles implemented with compatibility font/span pins; 11 new behavioral cases.
- B transactional CPU atlas-growth/packing and GPU preflight/growth integration implemented; six CPU growth tests
  plus two Windows free-quad-pool tests pass. Native forced-growth first-frame pixel comparison is being added.
- Debug and Release Engine.Tests: 386/386 passed (logs `.tmp-msbuild/memory-floor-engine-tests-{debug,release}.log`). An initial new fixture
  omitted parent topology journaling after direct detach/append; corrected the fixture without weakening parity.
- A2 image texture accounting now queries device allocation requirements; unknown page sizes disable admission,
  unknown standalone sizes are labeled and excluded from known-byte totals. Native query verification remains pending.
- Windows library Debug/Release builds: zero warnings/errors; Release Windows.Tests: 229/229 passed.
- Post-change Debug/Release VerticalSlice both report all 1,536 checks passed. PowerShell's merged-stderr wrapper
  returned 1 for these runs despite the suite's success summary; capture explicit native exit status on the final run.
- Exact baseline executable/PDB preserved at `.tmp-msbuild/memory-floor-baseline-native-20260912`; executable SHA256
  `0361F6D679F045AD71FCC05D10E0E0D06B3C0070D4DC259C2F8C0EB42F834061` matches the current baseline publish.
- Canon consistency passes. Full solution builds, native comparisons and long-load acceptance remain pending.

This plan extends [the existing optimization plan](cpu-memory-max-optimization-20260912-implementation.md).
Its implemented audio identity/SIMD paths, demand-driven meters, event-only audio manager, sparse snapshot
measurements and cached-detail live-ready retry remain the baseline. Do not implement them again. The next
CPU work remains that plan's P4 stable-topology capture; the emphasis here is a substantially smaller resource
configuration, including the graphics allocations identified by the subsequent native capture.

## 1. Decision and evidence

Start with two independent tracks: isolate submission-related graphics backing, and remove avoidable renderer
capacity. The former has the largest possible return but uncertain ownership; the latter has clearer ownership
and a lower-risk path to tens of MiB of reductions. Sub-100 MiB **total resident process memory** is an architectural
research target, not a credible promise from cache tuning. A first meaningful target is a reproducible reduction
of at least 50 MiB of owned retained allocations, followed by a separate native working-set comparison.

The authoritative evidence is [memory-capture-20260912-analysis.md](memory-capture-20260912-analysis.md), read in full.
The loss-free focused trace's retained virtual-allocation cutsets balance to 483.492 MiB: graphics 304.109,
NativeAOT GC 151.116, Windows heap 23.482, other 4.785. Within graphics, 176.094 MiB enters below
`CCommandQueue::ExecuteCommandLists`; the stack proceeds through Qualcomm, allocation backing and VidMm.
It does not identify the proprietary buffer's purpose. Image texture creation contributes approximately 37 MiB,
device initialization 33.087, GPU glyph atlas 16.004, stencil 8.008 and acrylic 7.938, all **within** graphics.

After further user navigation, the uninstrumented process's VMMap resident total was approximately 607 MiB,
including 104.441 MiB of image pages. A later same-PID diagnostic sample had GC committed 182.2 MiB, heap
171.5 MiB including 6 MiB fragmentation, 1,732 image entries / 524 ready, 40 MiB image accounting, 16 MiB
retained pixel pool and 103.7 MiB tracked GPU resources. These are different instants and overlapping counters.
They cannot be summed, subtracted into exact ownership, or compared as a matched optimization trial.

GC segment commitment stacks identify the allocation that triggered commitment, not the surviving objects in
that segment. The 16 MiB CPU glyph array is corroborated by code; scene/decode/hydration stack contributions
are leads for owner counters and retained-root analysis. Zero orphans and animation tracks rule out those
specific outstanding populations in the sample, not all possible leaks. No unbounded leak is established.

Additional same-PID evidence supplied by the orchestrator during this review: the startup line explicitly says
`UMA atlas pages unavailable stage=create hr=0x80070057 (ROW_MAJOR CPU-writable TEXTURE2D)`.
Later counters show zero atlas pages/cells and 463 `Image.Texture.Uma.64x64` allocations accounting for
28.9 MiB. Thus existing packing is implemented but unavailable on this adapter's current resource-creation
path. Raw 64-square BGRA pixels for 463 images are 7.234 MiB; approximately 21.7 MiB is the **arithmetic
opportunity within that tracked class**, not a measured saving or the complete driver overhead.

A later GC sample increased committed bytes to 216.1 MiB, heap to 205.3 MiB and fragmentation to 50.5 MiB
(Gen2 112.2 / LOH 90.8 MiB). Subtracting reported fragmentation gives approximately 154.8 MiB occupied heap,
versus 165.5 MiB in the previous 171.5/6.0 sample. Sampling/GC timing limits still apply, but this is evidence
against interpreting the new total as equivalent live-object growth. Array high-water and LOH fragmentation
need separate counters. These later figures are not substituted into the earlier ETL allocation balance.

| Priority | Work | Capacity reduction hypothesis, not measured WS savings | Decision condition |
|---|---|---|---|
| A | Submission experiment and exact resource census | 0-176 MiB of observed submission-triggered backing; upper bound is not a forecast | Distinguish device floor, workload retention and allocator retention |
| B | 2048 glyph start with safe growth; byte-bounded run reserve | 24 MiB CPU+GPU payload if 2048 fits instead of 4096 | No additional missing text or raster churn |
| C | Text-only snapshot projection; exclusive-slot trim; sparse pages if needed | 5-30 MiB initial planning range, replaced by measured array arithmetic | Savings reflect actual capacities and occupied pages |
| D | Unsupported UMA packing alternative, aggregate image/decode reserve and no-present maintenance | Up to ~21.7 MiB tracked small-texture padding plus 12-30 MiB reserve hypothesis; overlaps possible | Supported heap/texture posture, pins, cache misses and first-visible latency remain acceptable |
| E | Reclaim cold targets, stencil and optional renderer services | 8-25 MiB on workloads that leave those resources cold | Last-use fences and repaint validity proven |
| F | Metadata/view-model roots and code residency | Unpriced until root/module census | Remove specific duplicate/unneeded ownership |

Ranges overlap where one policy changes another owner's demand. Do not add the table into a process saving.
If A proves a large irreducible device floor, complete B-E anyway and evaluate a separate lightweight backend
prototype before investing in increasingly aggressive cache eviction.

## 2. What already exists, and the exact next changes

Paths prefixed `engine/` below mean `C:/wavee/fluent-gpu/src/`; `app/` means this repo's `src/apps/Wavee/`.
The applicable skills required subsystem ownership, render-thread COM confinement and orchestrator-only gates;
they also directed reuse of existing pools and policies. Those requirements shaped this plan.

| Existing owner | Evidence in current source | Next change |
|---|---|---|
| `engine/FluentGpu.Windows/D3D12/D3D12Device.cs` | `FRAME_COUNT=3`, `MAX_FRAME_LATENCY=1`; allocator creation near 1312, fence then reset near 1647, submit near 2092 | Measure allocation lifetime and command volume; keep banking constant initially |
| `ImageTextureStore.cs`, `engine/FluentGpu.Engine/Seams/Rhi/ImageAtlasPacker.cs` | Already packs <=128px into 1024-square pages; 256/512 textures pool with four free per bucket; UMA page create/map can disable packing | Report support failure and occupancy; improve existing packing only where measured |
| `GlyphRenderer.cs`, `engine/FluentGpu.Engine/Seams/Text/GlyphAtlasStore.cs` | One 4096-square R8 CPU mirror and GPU texture; four horizontal subpixel phases; existing adaptive staging | Make atlas edge a resource-lifecycle decision; retain raster quality and staging policy |
| `engine/FluentGpu.Engine/Scene/SceneRecordingSnapshot.cs` | Dense layout/paint/topology arrays grow by high-water index; text measurements are already sparse | Copy only text style from layout; trim claimed writable slots; page dense payload only if holes dominate |
| `SceneStore.cs` | `TrimExcessCapacity()` already trims all-free tail and preserves indices | Reuse it; separately report holes, live span and snapshot capacities |
| `OpacityLayerCompositor.cs`, `LayerTargetTrim` | Existing bounded pool, retained blur pins, frame-aged trim and fence retirement | Add wall-deadline eligibility and byte reserve; preserve actual last-use fence |
| `PixelBufferPool.cs`, `GpuMemoryBudgets.cs`, `AppHost.MaybeTrimOnIdle()` | Weak pixel cap 16 MiB and image cap 40 MiB; periodic idle entry already calls full pixel trim | Aggregate in-flight bytes, retain a small demand-based reserve, service once when idle without present |
| `app/Backend/Residency/MemoryGovernor.cs`, `app/App/Services.cs` | Priority-ordered shedding, registered image/detail/entity owners and UI-built pin set | Reuse registrations; queued retirement is not bytes already freed |
| `app/App/LibraryStore.cs`, `app/Backend/Persistence/CachedStore.cs` | Detail count cap 48 per dictionary; `ShedDetails` estimates 24,000 bytes/model; multiple bounded cold-presence/touch maps | Measure graph roots and shared payload; count cap is not byte ownership |

No product navigation or component tree changes are required. New observations belong in the existing
diagnostics/receipt flow; the engine publishes value snapshots to it:

```text
UI owners: SceneStore / ImageCache / LibraryStore
   -> copied owner counters -> existing Wavee diagnostics
Render owners: allocators / textures / glyphs / targets
   -> render-published counters -> same diagnostics
GC + VMMap/ETW -> external validation, separate from owner counters
```

## 3. A: separate driver floor from workload and allocator retention

Microsoft documents allocator reset as memory reuse after the GPU finishes; it is not a release-to-OS promise.
The current reset/fence sequence meets that basic requirement. Do not label this a per-frame allocator leak.
[ID3D12CommandAllocator::Reset](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12commandallocator-reset)

The orchestrator should add a deterministic renderer probe in the existing Windows test/probe composition
root, with an explicit command-line scenario. It is a diagnostic harness, not a production behavior switch.
Use the same ARM64 NativeAOT toolchain, adapter, driver, window pixels, DPI, refresh and resource initialization
as Wavee. A minimal scene must use actual clear/draw/submit/present, not headless RHI. Reuse synthetic images
and text so network hydration cannot confound the experiment. Keep three allocator/back-buffer/upload banks
and present latency one across baseline experiments.

Run distinct processes and timestamp the following transitions:

1. Window before device creation; device/queue/fences only; swapchain; first clear submit; 1,000 paced clears.
2. Minimal primitives, then fixed text, then fixed thumbnails; initialize only resources each stage actually needs.
3. Full synthetic Wavee scene, fixed scene continuously rendered, controlled navigation/scroll, then minimal scene.
4. After all old use retires, release explicit cold resources; wait without rendering; record retained backing.
5. In a separate diagnostic arm, replace allocator banks one at a time **only after their last-use fences and any
   recording references retire**. Separate a command-list replacement arm, then queue/device teardown arm.
   Recreate to the same resource and scene state before comparing. Device teardown changing every resource
   establishes only device ownership, not allocator ownership.

Capture no-submit idle as well as fixed present counts. Compare both commit and resident pages after the same
warm interval. ETW allocation/free events tied to the transition discriminate release from paging. Three or
more alternating runs are required before interpreting a material difference.

| Outcome | Interpretation and next implementation |
|---|---|
| Large backing appears on first clear and stays similar across scenes | Driver/device baseline candidate; backend comparison is warranted |
| Peak increases with draw/descriptor/barrier volume and survives return to minimal | Workload high-water candidate; reduce redundant command recording first |
| Fence-safe allocator replacement returns backing | Allocator-associated retention supported; prototype exceptional high-water bank retirement with long hysteresis |
| Only command-list or queue replacement returns backing | Keep owner label at demonstrated scope; allocator shrink is unsupported |
| Backing disappears only with textures/targets | Correct resource attribution; do not claim submission itself owns it |
| Counts grow with repeated identical cycles without a plateau | Escalate from floor optimization to a boundedness investigation with the new evidence |

Record actual `DrawInstanced`, barrier, descriptor bind, command-list submit, upload and target-switch counts;
counts in the portable DrawList alone do not measure driver command volume. Add scalar increments at existing
D3D12 emission chokepoints, not per-command strings. Existing command-state caching (`InvalidateCmdState` and
its bind helpers), span reuse, batching and image pages are the first mechanisms to extend if redundant work
is demonstrated. Preserve painter order, clip boundaries, glyph covering, layer scopes and resource barriers.

An experimental bank-retirement decision can be tested independently; thresholds are starting hypotheses:

```csharp
internal static class CommandBankRetention
{
    // Call only at a render-owned maintenance boundary with a CLOSED list.
    public static bool ShouldReplace(
        ulong completedFence, ulong lastUseFence,
        long nowMs, long lastHeavyUseMs, long lastReplacementMs,
        int peakCommands, int recentCommands)
        => completedFence >= lastUseFence
        && nowMs - lastHeavyUseMs >= 60_000
        && nowMs - lastReplacementMs >= 300_000
        && peakCommands >= 4096
        && (long)peakCommands >= 8L * Math.Max(1, recentCommands);
}
```

Do not ship this policy unless experiment 5 shows a repeatable material release. `peakCommands` is a proxy,
not a byte counter. Allocate replacement successfully before retiring the old bank; detach/reset or replace any
list still referring to it, retain failure recovery, and never add a synchronous fence wait just to trim.
No change to `FRAME_COUNT` is in this first implementation. A later 3-to-2 comparison needs all swapchain,
instance, descriptor, query, glyph and compositor banks audited together. It trades pipeline slack; it is not
equivalent to changing `MAX_FRAME_LATENCY`, and a one-bank solution can serialize CPU and GPU.

### A2. Fix accounting before resource budgets

`ImageTextureStore.CommittedBytes` currently rounds `GetCopyableFootprints` to 64 KiB. That measures copy-layout
storage, not necessarily the texture allocation requirement. `GetResourceAllocationInfo` returns adapter- and
driver-specific texture size/alignment; those values can include extra texture storage. It still does not expose
every driver allocation, so label it **resource allocation requirement**, not complete process graphics commit.
[Microsoft allocation-info contract](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12device-getresourceallocationinfo%28uint_uint_constd3d12_resource_desc%29)

Use the actual descriptor at resource creation and cache the answer in the owner's record:

```csharp
// Inside render-owned ImageTextureStore. Copy footprints remain for uploads.
private bool TryGetAllocationBytes(D3D12_RESOURCE_DESC* desc, out ulong bytes)
{
    D3D12_RESOURCE_ALLOCATION_INFO info = _device->GetResourceAllocationInfo(0, 1, desc);
    bytes = info.SizeInBytes;
    return bytes != 0 && bytes != ulong.MaxValue;
}
```

Query failure remains visibly unknown and follows resource admission/error handling; do not silently relabel
pixel arithmetic as exact. For placed resources, count each heap once, with occupied/free/retired subranges;
resource requirements inside it are explanatory subdivisions. Publish logical pixels, allocation requirement,
free-pool reserve and pending retirement separately. The app's cache budget estimate remains a distinct
portable admission quantity until a generation-checked render feedback path can reconcile actual placement.

## 4. B: make glyph residency proportional to the text actually used

Implement prior P5 using a 2048-square starting atlas, with 4096 growth when the current workload requires it.
That is 4 MiB R8 per CPU/GPU copy instead of 16, giving **24 MiB payload arithmetic** when the smaller atlas
fits. It is not a measured 24 MiB working-set reduction. Four subpixel phases, gamma, shapes, hinting, sizes and
lyric timing stay identical. Read-only code confirms the fixed edge also reaches staging row pitch and UVs;
changing the constant alone is insufficient.

Grow through a preflight/maintenance boundary before emitting this frame's glyph instances. A test harness
must first expose occupied shelf extent, required glyph dimensions including subpixel copies, miss count and
peak frame demand. Use the existing `GlyphAtlasStore(size)` and `GlyphStagingPolicy`; make ownership of a
replacement store explicit. Preserve the already-reduced warm upload reserve; do not count its past saving again.

```csharp
internal static class GlyphAtlasCapacity
{
    public const int InitialEdge = 2048;
    public const int MaximumEdge = 4096;

    // requiredExtent comes from the packing preflight, INCLUDING gutters/phases.
    public static int ChooseEdge(int currentEdge, int requiredExtent)
    {
        int edge = currentEdge;
        while (edge < requiredExtent && edge < MaximumEdge) edge *= 2;
        return edge; // caller must handle requiredExtent > edge via existing overflow policy
    }
}
```

Ownership sequence: preflight -> allocate new CPU store/GPU texture/descriptor -> populate every required
entry -> complete required uploads -> publish new realization generation and invalidate dependent caches ->
retire old resources after their last fence. The wider generation must increase even though a new
`GlyphAtlasStore` starts its own epoch at zero. Old and new resources coexist temporarily and both are charged.
If a safe first-frame population cannot be guaranteed, keep the valid old atlas and revise preflight; no new
blank tail or stale UV is acceptable. Do not shrink automatically until multilingual/DPI navigation demonstrates
a sustained small working set. Shrink hysteresis should be minutes, not a handful of frames.

For a more ambitious follow-up, implement the canon's multiple 1024-square glyph pages, keyed by
`(page, generation, cell)` with per-page CPU mirrors and fence-retired GPU resources. This can release cold
pages without rebuilding an entire 4096 texture, but adds descriptor/page batching and cache invalidation work.
Prototype after the smaller single-page win, not concurrently. Do not map glyphs onto the image atlas: formats,
subpixel packing, gamma and upload invariants differ. Never move a live cell under an in-flight instance.

Apply a global byte cap to existing `GlyphRenderer._quadPool` and shaped-run cache; current per-bucket counts
are not a total byte bound. Count live arrays separately from free arrays, drop only free capacity at idle,
and measure reshaping/raster time after each trim. Removing the CPU mirror entirely is a different raster/upload
design and is not included in the 24 MiB claim.

## 5. C: smaller render snapshots, with unchanged handle and publication safety

### C1. Stop copying layout inputs the renderer does not consume

The snapshot copies full `LayoutInput` at `SceneRecordingSnapshot.cs` around 451. The recorder's layout
read near `SceneRecorder.cs:2060` uses `TextStyle`; snapshot resource retention also uses its font-family
and span-run fields. Flex gap, margins, alignment and width constraints are UI layout inputs, not recording
inputs. Replace this snapshot column with text styling for text nodes using the **existing** `SnapshotColumn<T>`.
Keep `SceneStore.Layout` unchanged. Audit every consumer of the snapshot `Layout` API and delete that API
once migrated; do not retain a reconstructed full-layout compatibility path.

Concrete shape inside the existing snapshot owner (proposed, not compiled):

```csharp
private readonly SnapshotColumn<TextStyle> _textStyle = new();

// In CopyNode after paint is copied; Set matches the existing sparse-measurement idiom.
private void CaptureTextStyle(SceneStore source, NodeHandle node, int index)
{
    if (_paint[index].VisualKind == VisualKind.Text)
        _textStyle.Set(index) = source.Layout(node).TextStyle;
    else
        _textStyle.Remove(index);
}

public TextStyle RecordingTextStyle(NodeHandle node)
    => _textStyle.TryGet((int)node.Raw.Index, out var style) ? style : default;
```

Integrate Begin/EndCapture and removal with the existing sparse-column lifecycle. Migrate font/string and
span retention to this captured value; text references must remain pinned across skipped publications and
renderer-held frames. Change recorder local `li.TextStyle` reads to the captured style. Invalid handles are
still rejected by the recording surface's normal liveness check. Add a debug assertion for missing style on a
live text node instead of allowing a missing projection to become a silent default text style.

Savings must be calculated from `Unsafe.SizeOf<LayoutInput>() * oldLayoutCapacity` across the three slots,
minus sparse text values, dictionary capacity and free-slot reserve. Source comments calling NodePaint one
cache line and historical design sizes are not current sizeof evidence; `NodePaint` now has many fields.
Record runtime `Unsafe.SizeOf<T>()` values once in the native owner census, and assess cold-field splitting only
after that measurement. No need to vectorize copying bytes that can be removed entirely.

### C2. Reclaim high-water slack in writable slots

`SceneStore.TrimExcessCapacity` already preserves indices and only cuts the all-free tail. Snapshots have their
own grow-only arrays and need their own trim. At an explicit cold UI maintenance boundary, claim a slot with
the publisher's real state/generation CAS, release its previous image/span/string pins, and clear/rebuild its
incremental baseline before trimming. Never mutate a Reading slot, even if no GPU work is active: the renderer
can reread it for an animation-only turn. GPU fence completion does not release that CPU claim.

For each slot, retain at least its next capture's required highest reachable index plus measured headroom. The
following pure target calculation is useful for both census and behavioral gates; it is **not** authorization
to trim an unclaimed array:

```csharp
internal static int SnapshotTargetCapacity(int capacity, int highestRequiredIndex)
{
    int required = checked(highestRequiredIndex + 1);
    uint target = System.Numerics.BitOperations.RoundUpToPowerOf2(
        (uint)Math.Max(256, required));
    return target <= (uint)capacity / 2 ? checked((int)target) : capacity;
}
```

Two safeguards: trimming a currently spare slot must not force allocation inside a later protected paint
phase; preflight capacity before that boundary. Repeated large/small routes must not repeatedly trim and regrow:
use sustained slack plus long residence history, not one small frame. Include `_captured`, epoch arrays,
reference-id arrays, recording scratch, sparse dead payloads and reverse-publication arenas in the census;
releasing a dense array alone may leave the largest free payloads alive.

### C3. If holes dominate, page the payload rather than moving live handles

Tail trim cannot help when one live high index keeps a large mostly empty slab addressable. Report populated
rows and populated 128/256-row pages versus addressable capacity. If holes account for a material fraction of
snapshot bytes, replace dense payload arrays with page tables indexed by the same node index. Allocate pages
at cold topology-growth preparation; existing live indices never move. A page lookup is two array reads,
not a per-node dictionary lookup. Only sparse payload pages get storage; a small top-level address table remains.

Start with independent pages per snapshot slot, removing empty pages while its slot is exclusively writable.
Do not share mutable SceneStore arrays across the publication seam. A second-stage immutable page-sharing
design could reduce cold identical pages across slots, but must use detached immutable pages, explicit reader
leases and preallocated copies for changed pages. Renderer animation overlays remain private; never mutate a
shared page through an overlay accessor. Register any new cross-cutting contract in the engine canon first.
This is higher-risk work and should proceed only if C1/C2 plus measured hole bytes leave a worthwhile target.

### C4. Reduce LOH churn at the source

The latest 50.5 MiB fragmentation sample makes this a first-class measurement alongside retained capacity.
Do not equate the LOH generation's total with garbage or assume every hole is reclaimable. Record allocation
size histograms and owner growth/trim events, GC counts and post-GC live/fragmented bytes at matched states.
Repeated allocate-double-copy arrays followed by trim can reduce live bytes while increasing transient/fragmented
commit. Pre-sized bounded pools and fixed-size pages avoid repeatedly replacing large arrays; choose page
length from actual `Unsafe.SizeOf<T>()`, not a universal row count, and keep retained free pages byte-bounded.
For blittable bulk storage, existing native-backed arenas can be evaluated where stable-address ownership fits;
moving identical capacity off the GC heap alone saves no process memory. Reserve/commit pages should return
unused committed pages at cold boundaries while retaining virtual addresses and generation metadata.
Do not move reference-containing records into unmanaged storage, and do not replace ordinary arrays with
permanently pinned arrays as a supposed fragmentation cure. A one-off diagnostic collection may distinguish
dead objects from retained capacity in a dedicated probe; periodic forced collections are not the product fix.

## 6. D/E: aggregate reserves and reclaim without manufacturing frames

### D1. Image packing: verify it works on this machine

Current <=128px packing is already implemented. Read the existing UMA page create/map result and report
`AtlasImageCount`, `AtlasPageCount`, cells per bucket, page bytes, standalone count and failure HRESULT/stage.
A 1024-square page holds 225 64px cells or 49 128px cells with the existing gutter. One nearly empty page still
costs approximately 4 MiB of BGRA payload. Packing is beneficial at sufficient occupancy, not automatically at
every count. If the optional UMA page probe fails, do not count hypothetical atlas savings as implemented.

For this capture that failure is now **confirmed at creation**, HRESULT `0x80070057`; mapping was never reached.
Do not try a Map-only workaround or assume `WriteToSubresource` can make an invalid ROW_MAJOR descriptor valid.
Move the alternative allocation experiment ahead of lowering the image-cache budget: better allocation density
could preserve the same 463 ready thumbnails for substantially fewer tracked bytes.

For unsupported ROW_MAJOR pages, investigate supported placed-resource allocations as a separate prototype
using queried allocation requirements, actual heap compatibility, fences and driver tests. Small alignment
eligibility differs from ROW_MAJOR: standard small textures require UNKNOWN layout and cannot be render or
depth targets. New tight alignment is capability-dependent, so probe support rather than assume it on Adreno.
[Microsoft tight resource alignment specification](https://microsoft.github.io/DirectX-Specs/d3d/D3D12TightPlacedResourceAlignment.html)

The first prototype should retain independent UNKNOWN-layout thumbnails while placing eligible ones in a
bounded heap, preserving the current CPU-written, GPU-read-only resource posture. Query 4 KiB placement
eligibility before heap allocation; verify CUSTOM/WRITE_BACK/L0 compatibility and `WriteToSubresource` on this
actual adapter. A 64-square resource could require 16 KiB plus driver-specific storage rather than its present
64 KiB allocation. This is a capability experiment, not a guarantee that this driver accepts the combination.

```csharp
// In the isolated D3D12 resource probe; desc is an eligible non-RT UNKNOWN texture.
desc.Alignment = 4096;
var allocation = device->GetResourceAllocationInfo(0, 1, &desc);
bool smallPlacement = allocation.SizeInBytes != ulong.MaxValue
    && allocation.SizeInBytes != 0 && allocation.Alignment == 4096;
if (!smallPlacement)
{
    desc.Alignment = 0;
    allocation = device->GetResourceAllocationInfo(0, 1, &desc);
}
// Next: validate heap properties, allocate a bounded heap using queried alignment,
// then CreatePlacedResource at aligned, disjoint offsets. Reject unknown requirements.
```

The heap owner must enforce offset bounds, resource-generation checks and deferred slot reuse. Published
resources are immutable until last-use retirement; restaging acquires another slot. Slots can be reused within
a retained heap after completion, but the heap is released only when no live or in-flight resource remains.
Do not alias simultaneously live cells, rewrite a descriptor still referenced by GPU work, or take the known
Adreno copy/barrier path as an unmeasured substitute. Run the debug layer, repeated navigation, readback image
identity and device-loss stress before treating the prototype as a valid production option. If unsupported,
retain the proven allocation posture and continue the other savings; do not silently reduce image resolution.

Do not extend the atlas to 256/512 just to reduce rounding: those payloads already span whole 64 KiB units,
while coarser page eviction can increase memory. Fewer driver resources/binds might still help A; measure
that independently. A placed-resource heap reduces allocation granularity, but a heap holding one pinned
resource cannot necessarily return its empty space to the OS. No aliasing of simultaneously live resources.

### D2. One pipeline budget with separately protected live demand

Ready `ImageCache` entries do not each retain a decoded BGRA array: the synchronous pixel span transfers to
the sink. The real CPU owners are decode jobs, queued completions/uploads and the pixel pool. Budget their
combined bytes, preserving a lane for visible demand and charging actual pool bucket capacity.

Use weak-tier experimental ranges of 2-4 MiB free pixel reserve after a long quiet period and 24-32 MiB image
admission budget only if visible pins plus recent reuse fit. These are measurements to try, not shipping values.
Never lower beneath protected demand or drop a visible replacement before its last-good image is ready.
Retain current/outgoing swaps, snapshot refs, in-flight uploads and derived-source dependencies. Feed OS/DXGI
pressure into the existing governor and image owner; do not create a second independent trim loop.

Introduce a bounded admission lease before allocating each decoded bucket; return it after upload owns its
copy or cancellation completes. Queue length alone is insufficient when image sizes differ. A byte budget
must not deadlock a single permitted large job: reserve a bounded oversized-job lane while suspending prefetch.
Track queued encoded bytes, decoded in-use bytes, idle pool bytes, ready image estimate, GPU allocation
requirements and fence-retired bytes separately. Evicted entries can remain tombstones: `ImageHandle` is a
non-generational ID and parked nodes may repin it. Do not delete arbitrary unpinned IDs. Tombstone reclamation
requires actual handle-owner lifetime tracking or session teardown, plus stale async completion rejection.

### E1. Cold maintenance uses wall time for policy and fences for safety

`MaybeTrimOnIdle` already trims the pixel pool and scene tail approximately every 30 seconds when called.
If the host goes to indefinite sleep before the next threshold, no subsequent call is guaranteed. Layer pools
also age on frames, so submitting no frames can preserve cold targets indefinitely. Add one next-maintenance
deadline while eligible reserves exist; service it without acquiring a present credit or submitting an unchanged
scene. Cancel it when no reclaimable reserve remains. Minimized playback must continue normally.

Separate existing `HasPendingUploads` behavior into true upload work versus pending retire work when plumbing
this: `ImageTextureStore.HasPendingUploads` currently includes `_retired.Count`. A retire-only list should not
force presentation. Retirement may depend on a fence that is still progressing; wait/signal completion through
the render-owned maintenance path, without a polling render loop. Keep publication and device-loss wake races
under the same host wake mechanism.

Owners reclaim: free image textures/pages; completed upload/retire entries; unused layer targets and stale blur
pins; a stencil surface unused since the last stencil demand; optional acrylic/baked-blur scratch after demand
ends. Verify which are already lazy before changing initialization. Reuse `LayerTargetTrim`, extending it with
byte reserve and wall-time eligibility. Elapsed age does not authorize release of a resource whose last-use
fence is incomplete. Releasing the persistent canvas invalidates its ledger and requires a valid full repaint
on reuse. It can increase future full-frame work, so keep it warm while partial repaint demonstrably saves CPU/GPU.

## 7. F: metadata and code residency, without breaking the catalog

Capture a matched managed diagnostic heap solely for retained-root investigation, then confirm all savings
with native owner counters. Root by unique object identity: LibraryStore detail arrays, entity records,
membership/index arrays, projection lists, URI strings, image tombstones, HTTP/encoded buffers and cache maps.
A JIT diagnostic build has a different runtime floor; never use its total memory as the NativeAOT result.

`LibraryStore` caps two detail dictionaries at 48 and estimates freed bytes by multiplying removed entries by
24,000. That estimate can count payload still rooted in components or shared entity storage. Add actual
membership/capacity counters and distinguish estimated cache unlink from GC reclamation. Prefer sharing
immutable records and contiguous ID/index arrays where independently duplicated projections are proven.
Consider a byte-weighted detail LRU only after an ownership census; keep active route, queue/current track,
saved heads and hydration/live-retry semantics. Existing bounded cold-presence/touch maps are candidates only
if their real populations are substantial. Their maximum caps are not current allocations.

Audit resident image pages per public module and initialization stage; 104.441 MiB is resident code/data pages,
not the 45 MB Wavee executable size, and not a universal irreducible baseline. Lazy initialization can avoid
faulting unused code/data, but reducing mapped image size or moving allocations to another process does not
prove lower total system footprint. Do not unload native libraries with outstanding callbacks/threads/COM.
Private module internals are outside this investigation and no savings are assigned to them.

## 8. What a sub-100 MiB path actually requires

First specify the metric: private commit, private working set, total process working set and DXGI usage are
different. Use total process resident bytes for the ambitious visible-app target, and report private commit
alongside it. Do not satisfy the target by `EmptyWorkingSet`, periodic forced GC, hiding/minimizing or moving
the same work into a helper process. Also report any child process footprint separately.

An engineering sequence, with targets rather than predictions:

1. B-E: establish >=50 MiB less owned retained capacity with no audio/visual regressions; measure actual WS/commit.
2. A: if submission backing is reducible, target a <=300 MiB warm total-WS navigation workload. This requires
   measured driver and managed reductions; it is not guaranteed by stage 1 arithmetic.
3. If the fixed D3D12 floor dominates, build an **isolated** D3D11 renderer probe behind the portable RHI using
   identical image/text/raster work and presentation conditions. Compare driver baseline and dynamic workload;
   D3D11 is a hypothesis, not an established smaller backend. Keep CPU-ms/s, power, latency, blur, gamma and
   glyph fidelity in the decision. Do not begin a full backend port based solely on an empty-window win.
4. Only if the probe supports it, plan a complete backend implementation with the same retained-scene model,
   readback/pixel gates, device recovery, video interop and compositing. This would be a separate architectural
   project, not a production fallback added secretly to the current renderer.

For a useful feasibility check, a **hypothetical 96 MiB total resident budget** could allocate 24 MiB code/DLL
pages, 20 MiB driver/device/presentation, 8 MiB explicit text/targets, 12 MiB active/warm artwork, 20 MiB
managed scene+metadata+GC slack, 8 MiB audio/OS/native heap/stacks and 4 MiB reserve. None of those figures
is presently achieved, their categories must be disjoint in the actual census, and large physical windows
can exceed the presentation slice by themselves. Three 1920x1080 BGRA back-buffer payloads are 23.73 MiB
before other resources; three 4K buffers are 94.92 MiB. Buffer storage may not all appear in process WS in
the same way, so this is resource-capacity arithmetic, not a resident-byte subtraction.

This makes the go/no-go concrete: until code residency, driver/presentation and managed live data each fit
much smaller envelopes, sub-100 MiB visible Wavee with equivalent features cannot honestly be promised.
An audio-only minimized footprint is a separate workload and potentially a different feasible envelope.
Preserve the current audio quality, decode-ahead duration, endpoint latency and syllable-display cadence.

## 9. CPU work that complements the memory reductions

Carry forward prior P4: retain traversal/resource sets across stable topology and copy only changed rows at
lyric cadence, with exact fallback on topology/generation changes. C1 reduces bytes copied per changed text
row; it does not by itself eliminate the reachability walk. Reuse the existing capture stamps, publication
baselines, image references and animation overlays. Pixel changes still invalidate the correct glyph wipe,
geometry, layer and damage witnesses. No lower lyric frame rate, coarser syllable timing or scroll quality.

Use render-owned continuous animation for supported channels, change-only scalar publication, and existing
signal/binding APIs. Keep the completed event-only manager and meter-demand improvements. Profile remaining
audio-only visible CPU before pursuing more DSP SIMD: it does not reduce these large retained resources.
For paused/minimized operation, measure useful/empty thread wakes over minutes; the earlier zero-percent
12-second sample is encouraging but is not proof of zero work. Do not add a recurring cache timer after its
last reclaimable item is gone.

## 10. Gates, rollout and rollback

Implement disjoint ownership packages in this order: owner census/A probe; C1; B; D2/E1; C2; then whichever
of A/C3/F retains the best measured return. If the orchestrator uses parallel implementation agents, assign
disjoint files; only it builds, tests, publishes or launches. Keep all existing unrelated worktree edits.
Each package gets a precise before/after report before the next risky storage change.

Behavioral gates (never source-text tests):

- C1/C2: full/incremental snapshot parity, text style/span/font retention, recycled handles, text/nontext flips,
  renderer-held claim during UI edits/trim, skipped publications, overlay animation, parked-page return and
  high-index sparse scenes. Test capacity reductions directly and retain the hot-phase zero-allocation gates.
- B: full icon set, Latin/CJK/Arabic/combining/emoji, all subpixel phases, maximum lyric size, DPI/zoom ladder,
  pack-full preflight, old/new atlas generations in flight, failed growth, device loss, repeated shrink/grow.
  Pixel equality and no additional missing-text frames are required.
- D/E: unsupported UMA page create/map, finite pool bounds, sparse atlas occupancy, pinned/outgoing image
  swaps, canceled decode/upload, stale completion, multiple windows sharing device, stalled fence, no-submit
  maintenance, minimize/restore, optional-target recreation and canvas identity. Test retire-only work does not
  submit/present and that idle maintenance shuts off when finished.
- F: active catalog/queue pins, offline cached partial album -> live ready -> tracks load without manual
  navigation, repeated back/forward, large lists, logout/login and shared object ownership after eviction.
- A: same-resource before/after allocator replacement, no live-list or fence violations, present-credit
  balance, multi-window bank ownership, resource admission failure and device removal/recovery.

Orchestrator gates after each completed implementation batch, as applicable:

```powershell
# C:\wavee\fluent-gpu
dotnet build src/FluentGpu.slnx
dotnet build src/FluentGpu.slnx -c Release
dotnet run --project src/FluentGpu.VerticalSlice
dotnet run --project src/FluentGpu.VerticalSlice -c Release
dotnet test src/FluentGpu.Engine.Tests/FluentGpu.Engine.Tests.csproj
dotnet test src/FluentGpu.Windows.Tests/FluentGpu.Windows.Tests.csproj
# Only when docs/design changed:
powershell -File docs/design/check-canon.ps1

# C:\wavee\waveemusic
dotnet build Wavee.slnx
dotnet build Wavee.slnx -c Release
dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj
Invoke-Pester -Path ops/release/tests
```

Native comparisons use three alternating matched runs after 60 seconds warmup and 60 seconds steady
measurement, plus a >=30-minute navigation/playback/lyrics/minimize cycle and 100 repeat route cycles. Match
hydrated content, active lyrics, scrolled region, window pixels, DPI, adapter/driver, endpoint format and image
cache state. Record commit/WS, GC live/fragment/capacity, actual allocation/free cutsets, owner bytes and peak
transition bytes. Also record CPU-ms/s, allocation bytes/s, frame p95/p99, present cadence, first-visible art,
restore latency, raster/decode misses and audio underruns. Large mixed-script and maximum-window workloads
are separate labeled cases. Discard lost-event allocation traces for exact balance claims.

Acceptance requires a lower steady **and** bounded long-session plateau, lower attributable owner bytes,
no additional audio underruns, no text/image mismatch, and no regression outside baseline timing variability.
A reduced warm reserve that repeatedly reconstructs expensive resources is not a completed optimization.

Rollback is a reviewable reversal of the specific package's edits, preserving unrelated dirty changes.
Do not add environment variables or leave two production rendering paths to switch around regressions.
Retain the previous verified binary for matched comparisons. Diagnostic captures stay ignored and local;
the full user-navigation dump contains possible account/session data and is never uploaded.

Plan verification: source inspection and primary API contract review only. Snippets above are concrete proposed
integration/policy code, not a claim of compiled or tested production behavior. No measurable saving has yet
been attributed to this plan.

## Status 2026-09-12 evening

A (factorial driver probe) is complete; E1 render-owned retirement is not warranted by its evidence (nothing returns
inside a renderer-only process by design; the app's UI cold maintenance owns that). Two engine fixes landed from it
(edge-fade strip scratch 56 → 28 MiB; device-reported sizes for every render target and the glyph atlas) plus the
ToolTipClock one-shot timer. The candidate is `src\apps\Wavee\bin\publish-aot-tooltip\Wavee.exe`; live matched runs
are blocked on the user's running instance. See `handoff-20260912d-tooltip-edgefade.md`.
