# Scroll cover jank — findings and design (2026-09-15)

**Status: findings complete; no code landed.** App `C:\WAVEE\wavee-0.3`, engine `C:\WAVEE\fluent-gpu`.
Machine: Qualcomm Adreno X1-85, UMA, `tier=Weak`. Log: `%LOCALAPPDATA%\Wavee\logs\wavee-20260915.log`.

This is the design to implement next. It supersedes the 40 MB Weak image-cache cap in
`fluent-gpu/docs/plans/adreno-hang-fixes.md` M5 for **working-set size**. Hang mitigation that is still load-bearing
(1 decode-apply/frame on Weak, no `CopyTextureRegion` atlas on UMA, CoverShimmer Flat on Weak) stays.

Related earlier reports (do not treat as current on the budget numbers):

- `docs/plans/wavee/scroll-feel-investigation-2026-09-10.md` — compositor clock / external-monitor feel
- `docs/plans/wavee/gpu-memory-investigation-2026-09-11.md` — Weak budgets applied late; committed-byte accounting
- `fluent-gpu/docs/plans/adreno-hang-fixes.md` — DEVICE_HUNG cadence; mixed DedicatedVideoMemory with DXGI LOCAL

---

## 1. What the user sees

Wavee feels heavy while scrolling. Covers flash empty (grey 40 px tiles on Artist **Top tracks**) then pop in later.
The same album art is already on Home; opening the artist still shows blanks, then a stagger-fill.

That is **not** missing Spotify URLs. `Artwork` already returns a plain `BoxEl` when `url` is null. The tiles have
URLs; the GPU cache is full, decode is stalled, and the ones that do land fade for 110–220 ms while the host
force-repaints the whole window.

---

## 2. Log evidence (must not contradict)

| Observation | What it means |
|---|---|
| 340 `frame.slow` lines | The UI thread is regularly over budget |
| Artist worst: `LazyGrid` **11.78 ms** of a 15.2 ms frame, `repaintPct=100`, `ShelfCardHost×32`, `ToolTip×80`, `CoverShimmer×17` | One artist magazine frame realizes a full shelf row, ~80 tooltip components, and 17 loading tiles, then repaints the **window** |
| UI fps often ~120 while glass is not; artist `missedVblanks=69`; later `fps=69.5`, `missedVblanks=293` | Present/GPU miss the panel; “120 fps” is the UI loop, not the pixels |
| GPU 5–7 ms of an 8.3 ms refresh | The GPU is eating most of a 120 Hz slot **before** decode/upload work |
| `imageBytes=40.0` (cap), `images=487`, `imagesReady=212`, `decodeInflight=0` | Cache is **at the Weak cap**. 275 entries are not Ready. Nothing is decoding |
| Artist `pixelPool=16.0/16.0` | The CPU pixel pool is also at cap — that is why `decodeInflight=0` |
| Wake: `imagesPending=135–237`, `budgetDeferredVirtuals=75` | Visible virtuals are waiting on a full budget |
| `[d3d12] UMA atlas pages unavailable stage=create hr=0x80070057 (ROW_MAJOR CPU-writable TEXTURE2D)` | Atlas probe failed once; `_umaPagesDisabled` for the **session**. `imgatlas=pages:0` |
| `Image.Texture.Uma.256x256:35.9/115` | ~115 private 256² textures ≈ 36 MB of the 40 MB cap |
| `Image.PlacedHeap:4.3/6` | Placed-heap packing exists but only for 64/128; 256 never admitted |
| Damage sample **24 875 DIP** tall | Full-window damage on a tall artist page |
| `BackendUnsupported=86` | Partial-repaint disqualifiers (edge fade / layers) still in play |
| Adapter: `vramMB=128 sharedMB=16162 uma=True tier=Weak` | `DedicatedVideoMemory` carve-out vs ~16 GB shared |
| DXGI LOCAL: **`used:422.0 / budget:15394.5`** | Real WDDM process budget. ~3 % used. **Not** 128 MB |

Empty Top tracks = cache starvation + 1 GPU upload/frame + 120–220 ms fade, not a catalog bug.

---

## 3. Why the image cache is ~40 MB if DXGI says ~15 GB

Three different numbers were treated as one.

| Number | Source | Meaning |
|---|---|---|
| **15394 MB budget / 422 MB used** | `IDXGIAdapter3.QueryVideoMemoryInfo(LOCAL)` | Real WDDM process budget on UMA (= shared LPDDR). This is what `TryGetVramUsage` already returns |
| **128 MB dedicated** | `DXGI_ADAPTER_DESC.DedicatedVideoMemory` | Snapdragon reporting carve-out. **Not usable VRAM.** The `[d3d12.adapter] vramMB=` line prints this |
| **40 MB `ImageCacheWeak`** | `GpuMemoryBudgets.cs`, hardcoded | Hang-mitigation cap. **Never reads DXGI** |

`adreno-hang-fixes.md` treated 128 MB Dedicated as “LOCAL budget” and cut the image cache to 24 MB (later 40 after
committed-byte accounting). On this machine those two disagree by two orders of magnitude.

Consequences already in the running process:

- `EvictToVramPressure` waits for `used > budget * 0.90`. 422 ≱ 0.90 × 15394, so it **never fires**. That is
  correct once DXGI LOCAL is trusted; it must not be “fixed” by clamping budget to DedicatedVideoMemory=128.
- `EvictToBudget` only runs `if (_pumpCompleted > 0)` and only when `UsedBytes > 40 MB`. At exactly 40.0 MB with
  ~212 **pinned** Ready entries, new Visible thumbs stay Pending. Prefetch cannot yield: pinned never evicts.
- The hang was still real (UBWC `COPY_DEST→PSR`, upload bursts, thousands of 64 KiB resources). That is
  **cadence and packing**, not a reason to pretend the GPU has 40 MB.

Chromium (`cc/tiles/image_decode_cache_utils.cc`, confirmed 2026-09-15):

| Machine class | Decoded-image working set |
|---|---|
| Default | **128 MiB** |
| Low-end desktop (`IsLowEndDevice`) | **32 MiB** |
| RAM ≥ 4 GiB (non-Android renderer) | **256 MiB** |

This laptop has ~16 GB → Chromium would use **256 MiB**. Flutter `ImageCache` is 1000 entries / 100 MiB.
Wavee Weak 40 MB is ~⅓ Flutter and ~⅙ Chromium on this machine.

Unreal / D3D12 UMA guidance: DXGI LOCAL **is** system memory; engines target a fraction of that budget for the
**whole** renderer, not a 40 MB image sub-cap.

Coil/Glide: skip the placeholder→image fade only on a **memory** hit. Wavee additionally fades every first decode
(220 ms default, 120 ms “short”, 110 ms mid-scroll) and `AppHost` does `ForceFull(DetachedContent)` while
`HasActiveCrossfades` — so ten chart thumbs landing one-per-frame (Weak) keep the **whole window** dirty for seconds.

---

## 4. Atlas `hr=0x80070057`

`ImageTextureStore.CreatePageTexture` (UMA): 1024×1024 `TEXTURE2D` BGRA8, **`LAYOUT_ROW_MAJOR`**, `CUSTOM` +
`WRITE_BACK` + `L0`, `COMMON`, persistent `Map`, memcpy per cell, sampled as SRV.

`E_INVALIDARG` (0x80070057) on create is the documented “this GPU does not support a row-major **sampled**
texture.” Microsoft: some adapters allow row-major textures for **copies**, not for sampling. Adreno is tiled/UBWC.
One failed create sets `_umaPagesDisabled` for the session. Every thumbnail then owns a private committed texture
(64 KiB granularity). A 64² thumb is 16 KiB of pixels in a 64 KiB commit (~4× waste) plus one driver resource.

Private UMA textures **already work**: same CUSTOM heap, **`LAYOUT_UNKNOWN`**, `WriteToSubresource`. Glyph
“ROW_MAJOR” paths are **buffers**, not sampled textures.

ROW_MAJOR was required so a CPU write to one atlas cell cannot swizzle-stomp a neighbor the GPU is sampling
(`media-pipeline.md` §4.1a invariant I3). Without it, **patching a live 1024 page is unsafe**. Re-enabling
`CopyTextureRegion` atlas on UMA is how we got DEVICE_HUNG. Page-scoped publish (one apply/frame, waste a 4 MB
page per thumb) is worse than 64 KiB private.

**Design:** probe a ROW_MAJOR ladder (`Alignment=0`, then `65536`) before disabling. If still refused: keep the
placed-heap path (`SmallImageHeapPool`, UNKNOWN + WriteToSubresource, already works for ≤128) and extend it to
**256** when the 4 KiB query actually saves bytes; otherwise 256 stays committed private **behind a real byte
budget**. Do **not** re-enable GpuCopy atlas on UMA.

`SmallImageHeapPool.TryAcquire` today: `bucket == 64 ? 0 : 128 ? 1 : -1`. Cap 16 MB. 256 never admitted.

---

## 5. App-side amplifiers (same artist frame)

| Amplifier | Effect |
|---|---|
| Chart `decodePx: 96` while the slot is ~40 DIP | Same 128 GPU bucket as a paint-size decode (~72 px at 1.65×). Not the 40 MB killer by itself |
| `ShelfDecodePx = 256` on Weak | 256² × 4 = 256 KB committed vs 64 KB at 128. 115 private 256s ≈ 36 MB of the 40 MB cap. Same album as a 96/128 chart thumb = **two** GPU entries (`ResidentRenditionOf` exists but only helps if a larger copy is already Ready) |
| Artist videos/gallery `decodePx: 480` | Lands in the 512 bucket. Worse than shelves |
| Weak 1 apply/frame | Correct hang mitigation. Combined with 220 ms fades + ForceFull, covers *appear* to trickle |
| `pixelPool=16/16` | Blocks `Begin` even if the image cache had room |
| `ToolTip×80` | `Controls.Named` wraps cover FABs in a real `ToolTip` component; 32 shelf cards × play/more ≈ the census |
| `AutoEdgeFade` on opaque lists | `BackendUnsupported`; kills partial repaint. Artist main scroller already `EdgeCues = None` |
| Discography `overscanRows: 4` | Extra covers decoded off-screen on Weak |
| Track table `Overscan = 8` | Same, on album/playlist lists |

Flutter `ScrollAwareImageProvider`: if already cached, show during fling; else **don’t start decode** until
velocity drops. Wavee already throttles apply rate on Weak; it still **requests** every overscan/prefetch size
into a 40 MB cap until Visible starves.

---

## 6. Design (implement this)

### Engine (`C:\WAVEE\fluent-gpu`)

**1. Budgets (Chromium-class)** — `GpuMemoryBudgets.cs`

| Cap | Weak today | Strong today | After |
|---|---|---|---|
| Pixel pool | 16 MB | 32 MB | **32 MB both** |
| Image cache | 40 MB | 64 MB | **256 MB both** |
| Derived/blur | 8 MB | 16 MB | **16 MB both** |

Weak is no longer “strictly smaller.” The hang mitigations are cadence and packing, not a fake VRAM size.
`ImageSuite.cs` `gate.budgets.*` must be rewritten (today asserts 16/40/8, 32/64/16, and `weak < strong`).

`FluentApp.ImageCacheBudgetBytes` currently pins any budget that **equals** `ImageCacheWeak` so `FG_IMAGE_CACHE_MB`
cannot raise it. Once Weak == Default == 256, that equality would pin **both** tiers. Change the helper to not key
off the constant equality (allow the 16–1024 MB env override on both, or pass `weak:` explicitly and stop special-casing).

**2. Eviction** — `ImageCache.cs`

- `EvictToBudget` every `Pump`, not only when `_pumpCompleted > 0` (parked pages unpin; the next idle pump must shed).
- Visible admit at/over cap: evict unpinned LRU **before** refusing; never starve Visible; Prefetch may drop.
- `TryGetVramUsage` / `EvictToVramPressure`: keep DXGI LOCAL as-is (~15 GB). **Do not** clamp to Dedicated=128.
  Pressure at 90 % of 15 GB will not fire at 422 MB (correct). The 256 MB **image** LRU is what sheds covers.

**3. Atlas / packing** — `ImageTextureStore.cs`, `SmallImageHeapPool.cs`

- Probe ladder in `CreatePageTexture` before `_umaPagesDisabled`.
- On failure: placed-heap path; admit bucket **256** when `TryLayout` shows a 4 KiB query actually cheaper than
  default. Raise `MaxHeapBytes` if 16 MB is tight (policy test `SmallImageHeapPolicyTests` tracks the cap).
- **No GpuCopy atlas on UMA.** Keep Weak 1 apply/frame.

**4. Reveal** — `ImageCache.BeginReveal`, `AppHost.cs` ~4073

- `max(targetW, targetH) ≤ 128` and not derived → `RevealMs = 0`, `TextureMs = NaN` (same as `ImageTransition.None`).
  Mid-scroll thumbs snap; they must not keep `HasActiveCrossfades`.
- Derived / heroes (≥256) keep the authored fade at rest; existing half-length fade under `SuppressReveals` can stay
  for large art.
- `ForceFull(DetachedContent)` while `HasActiveCrossfades` is why a thumb fade repaints 24 kDIP. After thumb snap,
  only heroes keep that path. Do not ForceFull from a snapped thumb.
- Update `ImageSuite` 46f / 45e and `ImageSchedulingTests.ScrollReveal_*`: those tests use 16×16 / 64×64 and
  currently expect a 220 ms fade. Move the fade cases to **256×256**; add a gate that ≤128 snaps.

Derived entries have a default `SourceKey` (0×0). Snapping on `Key.TargetW` without `!e.Derived` would snap **hero
washes**. Guard: `!e.Derived && max(Key.TargetW, Key.TargetH) ≤ 128`.

### App (`C:\WAVEE\wavee-0.3`)

**5. Decode sizes**

- Chart (`Artist.UI.Chart.cs` ~509): drop `decodePx: 96`. Let `Artwork` use layout DIP × `Viewport.Scale` via
  `Design.ImageDecodeScale.For` (40 DIP @ ~1.65 → ~72, packable 64–128). Same for `Artist.UI.cs` 44 DIP row art.
- `Controls.Art.cs` `ShelfDecodePx`: **128 if `GpuProfile.IsWeak`**, else 256. Headless tests stay Strong → 256, so
  `ControlsTests` `ShelfDecodePx >= ShelfCardMax` still holds.
- Artist videos/gallery `decodePx: 480` → Weak uses the shelf cap (128), discrete keeps 480 or paint-size.
- `ArtworkFill` default 256 should follow the same Weak 128 rule in the method body (default args cannot be runtime).

**6. Census**

- Weak LazyGrid `overscanRows = 1` (discography today is 4). `LazyGrid` asserts `>= 1`.
- Track table `Overscan`: Weak 2, else 8.
- Skip `Controls.Named` → `ToolTip.Wrap` on Weak (cover FABs). Glyph-only names ride the tooltip because the engine
  has no accessible-name on a box; on Weak the census cost wins. Chart plays `ToolTip.Wrap` on compact counts: drop
  it (stacked rows already show `PlaysExact`).
- `AutoEdgeFade = false` on Weak for opaque page lists (`Track.Table.cs` ~1009, sidebar library scroll, rail
  panels). Artist main scroller is already `EdgeCues = None`. Horizontal chip rails can keep a fade.

### Docs to retcon when the code lands

- `fluent-gpu/docs/plans/adreno-hang-fixes.md` — 128 MB is Dedicated, not DXGI LOCAL; M5 image cap is cadence-not-size
- `fluent-gpu/docs/design/budgets.md` — Media Pipeline image-cache row 64/40 → 256/256, pixel pool 32/16 → 32/32
- `fluent-gpu/docs/design/subsystems/media-pipeline.md` §4.1a — probe ladder + placed-heap 256
- This file — mark landed + verification

---

## 7. Do not

- UMA `CopyTextureRegion` atlas (DEVICE_HUNG).
- UMA swapchain depth 2 without explicit consent (`adreno-hang-fixes.md` deferred).
- Treat compositor-clock latch as open (`lattice window=8.33 mode=pass` in this log).
- Clamp `TryGetVramUsage` to `DedicatedVideoMemory`.
- Snap derived/hero reveals via an empty `SourceKey`.
- Environment-variable switches for the new behaviour (repo rule). `FG_IMAGE_CACHE_MB` already exists as a discrete
  developer override; do not add a second one.

---

## 8. File map

| File | Change |
|---|---|
| `fluent-gpu/.../Scene/GpuMemoryBudgets.cs` | 256 / 32 / 16 both tiers; rewrite remarks |
| `fluent-gpu/.../VerticalSlice/Suites/ImageSuite.cs` | `gate.budgets.*`; reveal 256 vs snap 64 |
| `fluent-gpu/.../Windows/Hosting/FluentApp.cs` | `ImageCacheBudgetBytes` must not pin both tiers |
| `fluent-gpu/.../Scene/ImageCache.cs` | Evict every Pump; thumb snap; Visible-before-Prefetch |
| `fluent-gpu/.../Engine.Tests/ImageSchedulingTests.cs` | Scroll/reveal sizes |
| `fluent-gpu/.../Windows/D3D12/ImageTextureStore.cs` | ROW_MAJOR alignment ladder; placed 256 |
| `fluent-gpu/.../Windows/D3D12/SmallImageHeapPool.cs` | 3 buckets; maybe higher heap cap |
| `fluent-gpu/.../Windows.Tests/SmallImageHeapPolicyTests.cs` | Cap / 256 index |
| `fluent-gpu/.../Hosting/AppHost.cs` | Comments; ForceFull only for real fades |
| `wavee-0.3/.../Platform/Controls.cs` | Paint-size decode when `decodePx == 0` (ambient scale) |
| `wavee-0.3/.../Platform/Controls.Art.cs` | Weak `ShelfDecodePx` 128; `ArtworkFill` |
| `wavee-0.3/.../Platform/Controls.Cta.cs` | `Named` skip ToolTip on Weak |
| `wavee-0.3/.../Entities/Artist.UI.Chart.cs` | Drop `decodePx: 96`; drop plays tooltip |
| `wavee-0.3/.../Entities/Artist.UI.cs` | Drop 44 DIP `decodePx: 96` |
| `wavee-0.3/.../Entities/Artist.Page.cs` | Video/gallery 480 → Weak shelf cap |
| `wavee-0.3/.../Entities/Artist.Discography.cs` | Weak overscan 1 |
| `wavee-0.3/.../Entities/Track.Table.cs` | Weak overscan 2; Weak `AutoEdgeFade` off |
| `wavee-0.3/.../Shell/Sidebar.UI.cs`, `Rail.UI.cs` | Weak `AutoEdgeFade` off on opaque lists |

PlayPlay / `src/apps/.native/**` / `Wavee.PlayPlay/**` are out of scope.

---

## 9. Verify (orchestrator only — no parallel `dotnet` on the shared tree)

1. Engine Debug + Release (`src/FluentGpu.slnx`), VerticalSlice, `FluentGpu.Engine.Tests`, `FluentGpu.Windows.Tests`.
2. App Debug **and** Release `Wavee.slnx` (`TreatWarningsAsErrors`), `Wavee.Tests`.
3. On Adreno, live log:
   - `imageBytes` well under 256 MB in a normal session; `imagesReady` tracks visible, not stuck at ~212/487
   - `decodeInflight` non-zero while pending Visible exists; `pixelPool` not glued at 16/16
   - covers on first paint if that URL was Ready on Home at a resident size; else **snap**, not a 220 ms stagger
   - no `[d3d12.stall]` / device-lost
   - atlas: either `Image.AtlasPage.Uma` pages in single digits, or placed-heap/private under the 256 MB cap — not
     115× `Image.Texture.Uma.256x256` eating a 40 MB world
   - artist `frame.slow` census: ToolTip count collapses on Weak; `repaintPct` not 100 from thumb fades

---

## 10. One-sentence thesis

The Adreno is not a 40 MB GPU. DXGI says ~15 GB shared; Dedicated 128 MB is a label; the empty Top tracks and the
scroll jank are a **hardcoded hang-era cache**, a **dead ROW_MAJOR atlas**, **256 px shelves in 64 KiB-aligned
private textures**, a **full 16 MB pixel pool**, and **full-window repaint from thumbnail fades**. Fix the working
set and the reveal; keep the upload cadence.
