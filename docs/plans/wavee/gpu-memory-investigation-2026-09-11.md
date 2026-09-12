# Investigation — why Wavee burns GPU and memory on the Snapdragon (2026-09-11)

Machine: Snapdragon X Elite, Adreno X1-85 (UMA — GPU memory *is* system RAM), 120 Hz panel.
Builds looked at: the 0.2.9 Store package (`cproducts.Wavee_1.2.910.0`) and a local Release build of `main`.
Evidence: the always-on `[wake]`, `frame.slow`, `frame.churn`, `nav.frames` and `mem.sample` lines, plus
per-process GPU counters (`\GPU Engine(*pid_*)\Utilization Percentage`).

Reported symptoms: 50–68 % GPU and 520 MB–1 GB working set — with a video playing, with only lyrics open, and
with lyrics closed.

---

## Finding 1 — the render loop never idled (FIXED, verified)

**Symptom.** `[wake]` on the 0.2.9 build: `scrollAnim` held on **100 %** of ~140 loop runs per second, `sole:
scrollAnim` on 60–75 % of them, and `idleAgo` climbing monotonically to 2 113 s. The loop had not idled once in
35 minutes. This is why the GPU stayed busy with nothing moving, lyrics open or closed.

**Cause** (engine, `fluent-gpu/src/FluentGpu.Engine/Scroll/ScrollBarChrome.cs`). Leaving a page while its
scrollbar is still mid-fade — i.e. navigating within the 2 s idle-hide after any scroll — froze that row:

1. `Tick` skipped parked rows with `continue` **before** its liveness check, so the row was never dropped;
2. `_needsFrameCount = _active.Count` counted those frozen rows, so `NeedsFrame` stayed true forever;
3. the ticker kept its own `_parked` set, which nothing cleared when KeepAlive **evicted** the page — and a
   reused node index inherited it, freezing a *new* page's scrollbar.

It is the gap left by #136 (which fixed the hover/parked-body half of the same symptom).

**Fix.** Parking now lands the bar at rest (hidden, hover forgotten) and retires its row; "is this viewport
parked" is read from the scene's own `NodeFlags.Parked` instead of a mirror that can go stale.

**Verification.**
- New gate `gate.keepalive.parked-scrollbar-idles` (`NavSuite.cs`) drives the real KeepAlive path: scroll, leave
  mid-fade, evict, return. On the old code it **fails** (`parkIdle=-1 evictIdle=-1 wake=ScrollAnim`, the exact
  signature of the user's log); with the fix `parkIdle=0 evictIdle=0`, and the bar still reveals after an un-park
  and a cold remount.
- Full engine suite: **1505/1505 pass**. (`gate.arena.alloc-zero` failed once in a full run and passes in
  isolation and on rerun — an order-dependent flake, not a regression.)
- Engine and app, Debug and Release: 0 errors.
- Live: the rebuilt app's `[wake]` went from `run=4198/30 s` with `sole: scrollAnim=3265` to `run=534/30 s`,
  `sole: timer=277`, `idleAgo` resetting. **~140 fps of invisible work → ~18.**

---

## Finding 2 — every awake frame repaints the whole window (NOT fixed)

After Finding 1, measured GPU was still **63.8 %** (Wavee's own 3D engine; DWM adds 8 %) while the UI loop ran at
17 runs/s. Every `frame.slow` line carries `repaint=1.0` with `gpu=2–6.5 ms`.

**What is actually animating:** a `MarqueeHost` — the scrolling song title in the player bar. It is a looping
`TranslateX` keyframe row at `Cadence.At(60)` (`FluentGpu.Controls/Marquee.cs:202`) that never stops while the
title overflows. `nav.frames` reports `presented=480–497` per 4 s: **the render thread presents at panel rate**,
independently of the UI loop, and each present redraws the entire window.

Three engine mechanisms, each sufficient on its own:

| # | Mechanism | Code |
|---|---|---|
| A1 | A render-thread pose marks **every ancestor up to the root** `TransformDirty\|PaintDirty`; the recorder damages each such node's `SubtreeBounds`, and the root's is the window. | `SceneRecordingSnapshot.Animation.cs:240` → `SceneRecordingSnapshot.cs:502` → `SceneRecorder.cs:2678-2691` |
| A2 | Every clock-driven re-record is force-fulled outright. | `AppHost.cs:1002` (`DetachedContent`) |
| A3 | Rows are re-posed every tick even when held by their cadence or already `Done`, so A1 fires at 120 Hz for a 60 Hz row — and on every UI publication for a finished one. | `RenderCompositorAnimations.cs:156-177` |
| B | Any layer that is not plain `Opacity` (Blur, **both** EdgeFade classes, Acrylic) or any stencil clip disqualifies the whole frame from partial replay. Wavee always has edge-fades on screen (the marquee's own `FadeBand=24`, every `AutoEdgeFade` scroll view); lyrics adds blur. | `RepaintPolicy.cs:359-378`, consumed at `D3D12Device.cs:1686,1709` |

This contradicts the engine's own canon: `gpu-renderer.md` §13.1 promises *"animated transforms dirty only
old∪new bounds → a spinner repaints a tiny region."*

**None of the reference engines does either thing** (checked in the local checkouts):
WebRender, Chromium cc, Slint and XAML all damage only the changed leaf's old ∪ new bounds — an ancestor gets at
most a "walk into me" flag (XAML's `m_fNWSubgraphDirty`), never "I moved"; and WebRender/Chromium keep partial
repaint working with blurs by *growing* the dirty rect by 3σ, rendering the blur source over the grown rect and
writing back only the dirty part. Citations are in the engine plan doc.

**Two facts that make the fix smaller than it looks** (verified in the backend):
- `D3D12Device.CurrentScissorRect()` is already *innermost clip ∩ root damage*, and every layer composite goes
  through it — nothing in the blur/fade paths writes outside the replay rect today.
- The plain σ=0 edge-fade strip fade is **already structurally safe** under a clamp (`OpacityLayerCompositor.cs:1229`
  intersects each strip with the clip; the feather multiplies alpha and displaces nothing). Since Wavee's steady
  state has edge-fades but no blur, admitting EdgeFade alone unlocks the common case.

**Plan:** `..\fluent-gpu\docs\plans\damage-scoped-repaint-design.md` (Step 1 = A1–A3 with real code; Step 2 = B
with an ordered, risk-ascending change list). Expected effect: a marquee tick damages the title's strip instead of
the window; held ticks disappear entirely; lyrics, playhead and equalizer each repaint their own strip. Frame rate
and responsiveness are unchanged — only the pixels redrawn per frame change.

---

## Finding 3 — memory

`mem.sample` at 35 min (`ws=975 MB`): managed heap 318 MB (LOH 169, gen2 144, **112 fragmented**), tracked GPU
resources 171 MB (≈600 album-art textures = 88 MB), decoded-art CPU cache 64 MB + 32 MB pixel pool, and roughly
350 MB of other native (graphics driver, fonts, media/audio pipeline, the binary itself).

### 3a. Verified engine bug: the low-memory budgets never apply on this laptop

`ImageCache.cs:207` states the assumption outright — *"the backend has published `GpuProfile.Tier` by the time the
cache is constructed"* — and on the real startup path it is false:

- `GpuProfile.Tier` is published only at `D3D12Device.cs:1173`, during device init;
- device init runs inside the `AppHost` constructor (`FluentApp.cs:297`), but the pixel pool (`:289`), the image
  budget (`:294`) and the derived/blur budget (`ImageCache.cs:208`) all read `GpuProfile.IsWeak` **before** it.

| Budget | Intended (weak/UMA) | Actually used |
|---|---|---|
| Pixel pool cap | 16 MB | 32 MB |
| Image cache budget | 24 MB | 64 MB |
| Derived/blur budget | 8 MB | 16 MB |

The log proves the consequence: `pixelPool=32.0/32.0` and `imageBytes=63.8`. Fix: classify the adapter in the
device constructor, or construct pool + cache after the swapchain. **~40 MB, one ordering change.**

### 3b. Ranked consumers (reported by analysis; 3a verified, the rest to confirm before acting)

1. **Album-art textures, 88 MB.** Beyond 3a: each (url, width, height) is its own cache entry and texture, so one
   cover can be resident at 3–4 sizes; thumbnails appear not to share atlas pages (≈1 texture per image, 64 KiB
   granularity). Eviction runs only after a decode completes, not when a page parks.
2. **Scene arrays ×4, ~50 MB of LOH.** The store plus three render-thread snapshots are each sized to the store's
   high-water mark and never trimmed; the store itself only trims when the highest live node index drops below
   half capacity.
3. **Glyph atlas, ~44 MB** — a 16 MB managed mirror plus the 16 MB texture plus 12 MB of upload heaps. On UMA the
   mirror is avoidable.
4. **Fragmentation, 112 MB** — large transient buffers (`MemoryStream` + `ToArray` per Pathfinder response, etc.)
   churning the LOH; nothing ever compacts it.
5. **Gen2 holders** — audio chunks retained per track, entity store trimming only at 12 000 entities, 48 cached
   detail models.
6. **The memory governor effectively never fires** — it is driven by whole-machine memory load, which a 16–32 GB
   laptop rarely reaches, and only two arena priorities are registered.

~400 MB of private bytes sits outside the managed heap and tracked GPU resources; attributing it needs VMMap/ETW,
and the video pipeline is the prime suspect (its decode surfaces are not tracked at all).

---

## Corrections found while implementing

Two of this document's conclusions were wrong, and both mattered:

1. **The perpetual animator is not the player bar.** Both player-bar marquees are already `TriggerMode.Hover` and
   park when unhovered (`PlayerBar.cs:277,321`). The `Trigger.Always` + `Loop` + `Cadence.At(60)` marquee is the
   **now-playing row in a detail track list** (`DetailTracks.cs:2687`), with `WinampDeck.cs:108` a second. The
   engine diagnosis was unaffected — the equalizer (30 Hz, and present in card overlays, the sidebar row and the
   friends panel as well), the playhead and lyrics all hit the same path — but it is the wrong repro to hold on
   screen while measuring.
2. **The 88 MB of album art is arithmetic, not a broken atlas.** `ImageCache` charged decoded pixels (`w*h*4`)
   while `ImageTextureStore` commits a rounded-up square bucket: a 150 px cover charged 90 KB and committed 262 KB.
   A 24 MB budget times a ~3.5x over-commit is ≈ 84 MB, which is the observed figure, with no atlas failure
   required. The thumbnail atlas question is still open but is now worth at most the bucket-64 population, and the
   arithmetic says packing bucket 128 would be a ~31 % byte *regression*.

A third correction, to the engine rather than to this document: the layered partial-repaint route was capped at one
replay rect on the stated grounds that "a group RT is pool-leased, so the stream can only be walked once". That is
false — `Acquire` re-clears every lease and the clamp is value-level. The real blocker was that the backend's
layered submit interleaved per-frame work with the decode loop.

## What landed

| Finding | Status |
|---|---|
| 1 — the render loop never idled | **fixed**, gated (`gate.keepalive.parked-scrollbar-idles`) |
| 2 — whole-window repaint | **fixed** for the common case: pose-scoped damage, held/Done rows quiet, record elision, the layered rect cap lifted, and the σ=0 edge fade admitted to clamped replay. Five new damage gates; `--repaint-identity` 7/7 pixel-identical on the Adreno |
| 2 — blur under a clamp | **not done.** σ>0 blur and blurred edge fades stay vetoed, so a lyrics-open frame is still full. Needs the halo work (`damage-scoped-repaint-design.md` §2a-2c items 1-3) |
| 3a — weak-GPU budgets | **fixed**, gated at both tiers (`gate.budgets.*`) |
| 3b — album art | **fixed**: committed-byte accounting, weak cap re-tuned 24 → 40 MB, evict on page park |
| 3b — scene arrays ×4, glyph mirror/staging | **not done** |
| 3b — LOH buffers | **fixed** for the two worst paths (Pathfinder responses, compressed-body decode) |
| 3b — memory governor | **fixed**: process-footprint pressure + the priority-1 arena that was never registered |
| video pipeline | **fixed**: the warm engine is torn down after 30 s idle |
| the unattributed ~400 MB | **instrumented, not yet attributed** — `vram used/tracked/untracked` now prints, and `mem.sample` already carried `private=`; the measurement ladder (M0-M4) has not been run |

Not done: no live in-vivo capture on the Snapdragon. The headless gates and the on-device `--repaint-identity` probe
are the evidence so far; `repaintPct=` and the GPU counters on a real session are still owed.
