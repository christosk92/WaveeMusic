# Universal GPU default — one tree, all GPUs

**Repo:** `C:\wavee\fluent-gpu` · **HEAD:** `ac55069e5`  
**Plan:** `.cursor/plans/universal_gpu_default_1c5f044e.plan.md`  
**MUX tree:** `C:\WAVEE\microsoft-ui-xaml`  
**Symptom:** paused ≈ 0% CPU / 0% GPU; playing (EQ visible) ≈ 1.3% CPU / **56% GPU** (`engtype_3d`);
scroll ≈ 50–63%. CPU barely moves — the regression is GPU.

---

## Context

Cost is **rate × cost-per-frame**, and both terms are broken.

**Rate.** `SeekTicker`/`MediaSeekTicker` subscribe to `FrameClock.Tick` → `WakeReasons.FrameClockPoller`
(`AppHost.cs:1771-1773`), a bit present in **both** `GovernorNeverPace` (`:917-921`) and
`LatencySensitiveWake` (`:921-936`), so the ambient branch (`:1551`) and adaptive-FPS governor
(`:1562`) are both skipped. `AmbientPowerPolicy.cs:114` independently returns `Uncapped` on
mains+focused, making `AmbientCapEngaged` false (`:870-875`). And the EQ's continuous-float
`UseKeyframes(ScaleY, …, 850 ms loop)` plus `AnimScheduler.cs:197`'s unconditional
`TransformDirty` means skip-submit (`:3148-3155`, requires `!transformWrote`) can never fire.

**Cost per frame.** Every non-elided frame is a full RT clear + full DrawList replay + `Present` on a
`FLIP_DISCARD` **composition** swapchain (`CreateSwapChainForComposition`, `D3D12Device.cs:738`).
`FrameInfo.Damage` feeds only the acrylic cache — it never scissors. The opaque fast paths exist but
stay dark: `IsOpaquePlainRect` (`:1726-1730`) needs α=1 **and** square corners **and** `ClipW<=0`;
occlusion cull (`SceneRecorder.cs:645-655`) needs the same plus non-`ClipsToBounds`. Wavee's ladder
keeps α<1 by contract, the content pane has a rounded TL, and its rounded `ClipToBounds` stamps
`ClipW` on every descendant (`ApplyRoundedClip`, `:1676-1698`; inheritance at `:1663-1664`).

**What changed this round.** An earlier draft declined an opaque content plate to preserve live Mica.
That decision is **reversed** — see below; WinUI itself does not keep live wallpaper under opaque
content, so B4 is parity, not a compromise.

### B4 rationale — verified against the MUX tree

`C:\WAVEE\microsoft-ui-xaml\specs\xaml-backdrop-api.md:46-47`, verbatim:

> This backdrop is what will render behind the content specified in `Window.Content`.
> **If all of the content is fully opaque, this backdrop will have no visible effect.**

The spec's own example makes the policy explicit: `NavigationControls` has a transparent background
*"so the Mica backdrop will be mostly visible … in the margins and gaps"*, while
`<local:ContentArea Background="White"/>` is *"an opaque background, so the Mica backdrop won't be
visible"*. Mica is a DWM/system-Composition backdrop, not re-rasterized into the app RT per frame
([`docs/design-notes/mica-desktop-acrylic.md`](C:/WAVEE/microsoft-ui-xaml/docs/design-notes/mica-desktop-acrylic.md)),
and FluentGpu already matches that host path (`docs/design/subsystems/window-backdrop-mica.md`).

So B4 steals WinUI's **content paint policy**, not its `HWCompNode` dual Visual tree (explicitly
rejected in `docs/design/foundations.md`).

Further MUX context (why WinUI stays cheap on tiny animations — not required for B4, but explains the gap):

| WinUI | FluentGpu as-built |
|---|---|
| Sparse retained WUC Visual tree + SpriteVisual surfaces | One UI swapchain; DrawList → full clear/replay/Present |
| Independent anim on compositor thread | UI motion still forces app Present |
| Opaque content retained; dirty subgraphs only | Translucent content ⇒ fullscreen alpha with DWM each Present |
| Mica = sibling system Visual | Mica = show-through under full redraw |

---

## Three corrections to earlier handoff drafts (verified)

**1. `ShellMergedRungTests` does NOT need updating.** All four tests assert *token algebra*, never the
paint site: three compute `merged` locally from palette values, and
`ContentPaneMerged_IsTheActiveThemesTwoRungs` asserts on the `WaveeColors.ContentPaneMerged` **token**
plus the raw rungs. Changing what the shell *paints* leaves all four green **provided the
`ContentPaneMerged` token stays defined** (`WaveeTokens.cs:78`). Keep it. This means the existing
tripwire survives B4 intact — see Guardrails.

**2. B4's blast radius is two lines, not a rework.** `ContentPaneMerged` is painted at exactly **one**
site: `WaveeShell.cs:584` (`Fill`), with `:585` (`Corners = ContentPaneCorners`). That node
(`:581-590`) is the static pane underlay and is a **sibling** of the clipping card (`:591-620`), not a
descendant — so it is not itself under the rounded clip, and blocker #3 (`ClipW` stamp) does not apply to it. It needs
only α=1 and square corners.

**3. The rounded TL is deliberate WinUI parity, so squaring it needs a patch.** `WaveeShell.cs:612`
cites *"Stock NavigationViewContentGridCornerRadius = 8,0,0,0: only the corner facing the nav pane
rounds."* Going square is a visible regression unless the cut-away is restored — use the same
technique already in this file for *"(a) the top-left CORNER CELL"* (`:565-570`): an opaque square
underlay plus a small rounded corner piece. The clipping card at `:615-616` keeps the rounded clip, so
page content still rounds.

---

## A — Present rarely

| ID | Work | Files |
|---|---|---|
| **A1** | `SeekTicker`/`MediaSeekTicker`: `FrameClock.Tick` → pixel-due `UseInterval` (`durationMs/max(width,1)`, clamp ~`[33,250]`), keeping `Recompute`'s pixel gate (`SeekBar.cs:70-74`). `AmbientPowerPolicy.Apply()`: never `Uncapped` — `HalfRefresh` plugged+focused, ~30 otherwise; fix docs at `Program.cs:300-307`. `EqBar`: drop `UseKeyframes` → ~30 Hz `UseInterval`, device-pixel quantized, bound `Transform` (the `SeekBar.cs:136-137` pattern), keep `TransformOriginY=1`. Gate `animate` on real visibility (`TrackRow.cs:779`, `MediaCard.cs:1377`/`:1416`, `FriendsPanel.cs:131`). | `SeekBar.cs`, `MediaSeekBar.cs`, `AmbientPowerPolicy.cs`, `Equalizer.cs` |
| **A2** | Value-gate anim compose — drop the unconditional `Mark(TransformDirty\|PaintDirty)`. **Note:** after A1 the EQ leaves the anim slab entirely, so A2 no longer helps *it*; its value is every other retained animation (hover/press fades, springs at rest) that currently dirties a node per tick with an unchanged value. | `AnimScheduler.cs:197` |
| **A3** | Stop presenting when covered. **Caveat:** the primary swapchain is `CreateSwapChainForComposition` (`D3D12Device.cs:738`), and `DXGI_STATUS_OCCLUDED` is generally *not* returned on composed swapchains — it is an HWND-flip-model signal. Key off `DWMWA_CLOAKED` + window visibility, and **verify `OCCLUDED` actually fires before relying on it**. Present's result is currently only tested `< 0` (`:2656`), so a positive status is silently dropped either way. | `D3D12Device.cs` Present path |
| **A4** | Wire `Cadence`/`NextDueMs`: playhead `Driven`, ambient `Hz`. Types exist (`AnimClock.cs:58-93`) but `NextDueMs` is a stub `HasActive ? 0f : ∞` with **zero callers**. This is the principled replacement for the `FrameClockPoller`-uncaps-ambient heuristic. Structural insurance, not required for the first win. | `AnimClock.cs`, `AnimScheduler.cs`, `AppHost.cs` |

**Do not demote `FrameClockPoller` from the wake masks** — lyrics karaoke
(`LyricsView.cs:2878-2911`) legitimately needs panel rate.

## B — Present cheaper

| ID | Work |
|---|---|
| **B4** | Content pane → `WaveeColors.FloatingPane` (`Flatten` through both rungs) + square corners + TL corner patch. One `Fill`, one `Corners`, one small added node (see correction 2/3). Chrome — titlebar, sidebar, player bar — **stays live Mica** (α<1). |
| **B1** | `ApplyRoundedClip` (all three overloads, `:1676-1698`): skip the stamp when the instance's device AABB lies inside `inset(RoundedRect, Radius + aaSlack)` and is axis-aligned; stamp on doubt. Pixel-identical. `RoundRectPipeline.cs:240` already branch-guards the clip SDF, so standalone this is ALU relief — its real value is unlocking the opaque PSO for interior fills once B4 lands. |
| **B2** | Right-rail double-coat — **verified real, and a visual bug**. `WaveeShell.cs:719-720` paints the floating backing band `FloatingPane` (both rungs, opaque), then `RightRail.cs:63` paints translucent `FileArea` **on top**, so a floating rail is one pane-coat darker than docked — exactly the desync `FloatingPane`'s docstring exists to prevent. Fix: backing band paints `FloatingChrome` (opaque *plate*) and `FileArea` completes the ladder. Also check: `FloatingPane` builds rung 2 from `Active.Content` while `ContentPaneMerged` uses `Active.FileArea` — if those differ, one is wrong. |
| **B3** | Occlusion cull: conservative inset for rounded opaque children (`SceneRecorder.cs:634-674`), so an opaque rounded pane can occlude what it fully covers. This is what converts B4 into a **scroll** win, not just a cheaper pane pass. |

## C — Present path discipline

Skip-submit stays the default strategy on **all** GPUs. No `FLIP_SEQUENTIAL` tiled partial present as
default — measured regression on Adreno TBDR (preserving the back buffer forces tile loads that cancel
the skipped redraw). `Present1` dirty rects remain a possible future **discrete/IMR-only** capability,
never a fork or a per-vendor branch.

> **SUPERSEDED IN PART (2026-08).** Two statements above have been overtaken by the §5.1 campaign; the
> `FLIP_SEQUENTIAL` and `Present1` rulings still stand exactly as written.
>
> - *"Acrylic `Damage` stays blur-cache-only"* — no longer true, and the distinction it drew has a name now.
>   `FrameInfo.Damage` (the acrylic blur-cache union: transform-moved nodes only) is **unchanged** and is
>   still blur-cache-only. What landed is a **separate, deliberately different** set,
>   `FrameInfo.RepaintDamage`, which drives damage-scissored repaint into the engine-owned persistent
>   canvas. The two answer different questions and must never be substituted for one another.
> - *"partial present is a future, discrete/IMR-only capability"* — true of **`Present1` dirty rects**, which
>   is what that sentence is about, and still the ruling. It is **not** true of partial *repaint*: the landed
>   mechanism never touches the present call. The frame is scissor-repainted into an RT **we own** (so
>   retaining its contents is legal, unlike a `FLIP_DISCARD` back buffer) and then blitted whole to the back
>   buffer, so the present path is byte-for-byte what it was — which is precisely why the Adreno regression
>   above does not apply. The floor is that full-surface blit, and the high-coverage / replay-unsafe fallback
>   is today's straight-to-back-buffer path verbatim.
>
> Contracts and route policy: [`docs/design/subsystems/gpu-renderer.md` §13.1/§13.1a](../design/subsystems/gpu-renderer.md).
> No speed multiplier is claimed — the hardware measurement is still pending (see
> [`wavee-gpu-counter-recipes.md`](./wavee-gpu-counter-recipes.md) for the recipe).

## D — Beauty constraints

Chrome keeps live Mica. Content is an opaque Mica-toned plate (WinUI policy). Motion runs on
compositor binds + springs. Ambient motion is cadence-classed. Kernel blur stays off the hot ambient
path.

## E — Prove

Fix `FG_GPU_TIMING` before quoting any GPU number from it: the `mid` stamp is taken before the
back-buffer transition (barrier + clear + queue stall bill to `CatFill`) and `[fps]` prints
`_lastGpuRenderMs` with no freshness check. Gates: `rectInstOpaque`/`rectInstBlended`
(`D3D12Device.cs:1133`), skip-submit rate (`FramesSkippedSubmit`), wake sole-reason
(`FG_WAKE_DIAG`), scroll p95. Measure with **continuous per-PID `Get-Counter` timelines**, never Task
Manager's 1 s smear or disjoint snapshots.

Mine `stash@{0}` only for the `FG_GPU_TIMING` fixes if useful — do **not** land its partial-present path.

---

## Ship order

**A1 → B4 → A2 + B1 + B2 → E → A3 → B3 → A4**

A1 first because it is the only item that attacks the reported symptom in the reported configuration
(rail closed, EQ visible) and is a confident ~4×. B4 second because it is the largest per-frame lever
and everything in B1/B3 compounds off it.

## Expected (why each step, with target numbers)

Targets are Adreno maximized `engtype_3d` continuous Get-Counter averages — order-of-magnitude
goals that justify the work; E confirms.

| Milestone | Playing + EQ | Scroll | Idle | Why we do it |
|---|---|---|---|---|
| **Today** | **~56%** @ ~120 Hz Present | **~50–63%** | **~0–1%** | Full-window translucent RMW every non-elided frame |
| **A1** | **~8–15%** (~4–7×) | unchanged (~50–63%) | ~0% | Stop free-run: pixel-due seek + paced/quantized EQ → skip-submit can fire; ambient cap engages. Attacks the *reported* bug. |
| **+B4** | **~6–12%** | **~30–40%** (~1.5–2×) | ~0% | WinUI opaque content plate: one full-region pass flips blended-SDF → opaque no-blend (~1 screen of fill). Wallpaper-through-content gone (MUX-same). |
| **+B1** | **~5–10%** | **~25–35%** | ~0% | Interior fills drop inherited `ClipW` → opaque PSO unlock for α=1 square children; ALU + more no-blend instances |
| **+B2** | n/a (rail closed in report) | floating rail parity; docked −1 blend pass if merge lands | ~0% | Fix floating double-coat visual bug; remove redundant translucent overpaint |
| **+B3** | — | **~15–25%** (~2–3× vs today) | ~0% | Occlusion finally culls under the opaque pane — *this* is the large scroll win B4 alone cannot deliver |
| **+A2** | small (EQ already off slab) | small | ~0% | Hover/press/springs at rest stop false `TransformDirty` → more skip-submit |
| **+A3** | — | — | covered → **~0%** | Stop Presenting when cloaked (composition OCCLUDED may not fire — cloak path) |
| **+A4** | stays near A1 floor as more ambient ships | scroll still display-rate by design | ambient can’t free-run again | Structural insurance replacing FrameClockPoller heuristics |

**Read the table as justification, not a contract:** A1 is the confident ~4× on the playing spike; B4 buys ~one screen of opaque fill; B3 is required before claiming scroll halved. E measures; revise numbers, don’t skip the work.

NVIDIA/AMD dGPU: same levers, usually lower system pain; no per-vendor branches.

---

## Verification

```powershell
dotnet build src/FluentGpu.slnx
dotnet build src/FluentGpu.slnx -c Release      # both arms — diag consts compile differently
dotnet run --project src/FluentGpu.VerticalSlice
```

- **A1:** `FG_WAKE_DIAG=1 FG_FPS_LOG=1`, playing with lyrics closed → no `sole: frameClockPoller`;
  `AppHost.FrameClockPollerCount == 0`; `[fps]` `wait` token ambient-class.
- **B4:** `d3d12.rectInstOpaque` goes 0 → nonzero. Eyeball the TL corner cut-away and confirm chrome
  (titlebar/sidebar/player bar) still shows live Mica.
- **B1:** interior instances away from the TL corner carry `ClipW == 0`.
- **B2:** floating rail colour now matches docked.
- **GPU:** continuous `Get-Counter '\GPU Engine(*)\Utilization Percentage'` filtered to the Wavee PID
  across paused → play (EQ visible) → scroll → stop.

Not regressions: `engtype_videodecode` ~10% is a separate always-on issue.

## Guardrails

- **No `FG_*` kill switches** for new default behaviour (standing user ruling).
- Lyrics keep `FrameClock` / panel rate; wake masks untouched.
- **`ShellMergedRungTests` must stay green untouched.** Per correction 1 this is achievable through
  B4 — keep the `ContentPaneMerged` token defined and change only the paint site. If a change forces
  an edit to `MergedRung_StaysTranslucent_SoMicaStillReadsThrough`, stop: that means the *token*
  algebra or a chrome contract is being altered, which is out of scope.
- Do not land `stash@{0}` partial present; mine it only for the `FG_GPU_TIMING` fixes (E).
- User runs the app; verification is build + VerticalSlice + the documented counter recipes.

---

## One-line for the next agent

**WinUI keeps Mica cheap by painting opaque content over a DWM backdrop; FluentGpu already has DWM Mica but Wavee's translucent content + Seek/EQ free-run makes every Present pay fullscreen RMW — ship A1 then B4 (FloatingPane + square + TL patch, token tests untouched), then B1/B2/A2, measure with fixed timing, then B3 for the real scroll cull; never land TBDR partial present as default.**
