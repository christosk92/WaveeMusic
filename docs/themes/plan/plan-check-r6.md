# Targeted verification of technical plan revision 6 (2026-10-11)

Scope: R4-B1 fix, the twelve R4 majors, the board inventory, NQ10. Code: `origin/main` WaveeMusic `b665627e`,
fluent-gpu `9aff04403` (read with `git show`). Boards: designers' final copies in
`%LOCALAPPDATA%\Temp\claude\C--wavee-fgpu\21bc0c72-...\scratchpad\{classic,zune,pano}\project\`, WinUI in the plan
session's `canvas-v2\winui\project\`. Factual fixes were applied in place and logged in the plan's new **A.6**. A
backup of the plan as it was before this check is at `...\21bc0c72-...\scratchpad\edits\technical-plan.rev6.bak.md`.

**Verdict: ready for owner approval.** No blocker is left. One small spec gap (D1) should be added to E22 before
EP22 is planned. NQ12 and NQ13 are new owner questions, each with a default.

## 1. R4-B1 (pending tier + `CommitPendingTiers()`): holds

- The pending value lives in a host table, not in a signal, so none of the in-frame flushes can apply it:
  `FlushToQuiescence` `:6319`, `FlushRebindsToQuiescence` at `:6324`, `:6379`, `:6400` (the `SyncScrollPlansMidFrame`
  path `:6396-6405`), the flow pass `:7716`, and the parked-frame flushes in `RunFrame` `:5688`, `:5704`. Only the
  commit step writes it, and that step is skipped when `resized` is true. Two resized frames in a row therefore both
  keep the old tier.
- Commit point: capture is gated on `willReconcile = _runtime.HasPending || _needFullLayout` (`:6176`) and `!resized`
  (`:6180`). The commit has to run **before `:6176`**, after `RunScrollFrame` (`:6167`). If it ran after that line, a
  frame whose only work is the tier write would skip the capture and then snap at `:6319`. The plan said "~:6176";
  §3.3 rule 3 now states the exact place (A.6 #8).
- `TierCrossingTests` against the headless window: `ClientSizePx`, `Scale` (raw DPI), `SetZoom`, `InModalLoop`,
  `SizedInModalLoop`, `Composited` and `ModalLoopTick` are all settable (`HeadlessPlatform.cs:66-94`), and `Paint(0,
  keepAlive)` is public (`AppHost.cs:6044`). `DeferModalResize` = keepAlive && InModalLoop && Composited (`:8289`), so
  the drag deferral can be reproduced. Of the five new cases:
  - two consecutive resized frames: writable;
  - the end of a virtual list: writable;
  - `Composited = false`: writable;
  - plain-write negative control: writable;
  - **resize during a freeze: not writable in EP22.** E2 lands in EP10, after EP22. A.6 #9 moves the case to EP10.
- **D1 (spec gap, not edited).** Nothing makes a frame run after the resized frame. If the settle paint at
  `WM_EXITSIZEMOVE` leaves a tier pending and nothing else is awake, the loop goes idle and the crossing waits for an
  unrelated wake. Also, inside the modal loop, the keep-alive idle skip (`AppHost.cs:~6096-6105`) returns before the
  commit step. The tests drive `Paint` by hand, so they would not catch this. Default: storing a pending tier calls
  `WakeFrame()` and raises a wake bit that `NoEssentialModalWakeReasons` does not mask, and one test drives `RunFrame`
  instead of `Paint`.

## 2. R4 majors

| Finding | Holds? | Note |
|---|---|---|
| zoom-a | holds | `min(Suggest, plateauDown(W/1920))` is a min of two non-decreasing functions. For W < 1920, Auto's `Suggest` is always 1.0 (W/1600 < 1.2 snaps to 1.0), so the two arms meet at 1.0. Layout width W/z ≥ 1920 for every W ≥ 1920. Matches the revised NQ1: today's curve, never pushed down into Standard |
| zoom-b | holds | the 1920 × 1056 and 2048 × 1114 test sizes are correct for a maximized window with a 48-DIP taskbar; the NQ10 wording was corrected (§4 below) |
| zoom-c | holds, corrected | the zoom write gives one more resized frame (`SetZoom` folds into `Scale`, `Win32Platform.cs:864-871`; `EnsureSize` compares `Scale`), so the old tier is presented for **two** frames, not one (A.6 #7). See D2 |
| h0a | holds | `App.cs:88` `Platform.Boot`, `:142` `InstallMarshallers`, `:147` `AcquireInstance` confirmed; `Shell.Run` is `:232` (A.6 #10); the poster seams at `Shell.Host.cs:368-396` confirmed |
| h0b | holds | a separate process for the static boot state; nothing in the code contradicts it |
| lp1 | holds | `Lyrics.Prefs.BlurStrength(bool onStage)` is at `Shell\Lyrics.cs:1425` |
| hyst | holds for WinUI, Zune and Panorama; **wrong for Classic** | `C-Breakpoints` holds 40 on widening at 528 and 1008 and on narrowing at 1920. `TierEdge(At, NarrowHold, WidenHold)` can express this, so no model change; text corrected (A.6 #4); direction effect → NQ12 |
| pane | holds | `Z-Sizes-Breakpoints` is byte-identical to the copy that was read. New: Classic Huge also docks two panels (Lyrics + Queue), added to §3.1 (D3) |
| panel | holds | `ZF-Breakpoints` is byte-identical; Panorama's edge is 1008 |
| short | holds | 330 − 48 − 72 = 210; − 88 − 72 = 170; − 48 − 84 − 72 = 126; − 48 − 88 − 84 = 110 (130 with the 64 mini bar); Classic 226 with the 56 mini bar. No short board is on disk yet |
| pt2 | holds, now moot | all 13 Panorama pages are drawn at Standard + C/S/H |
| split | the rule holds; the inventory was stale | corrected from the final folders (A.6 #1) |

Monitor table, maximized: all six rows are arithmetically right (the taskbar changes none of them). Today vs with the
clamp:

| Monitor | Today: zoom → layout | With the clamp: zoom → layout |
|---|---|---|
| 1080p at 100 % | 1.0 → 1920 | 1.0 → 1920 |
| 1440p at 100 % | 1.5 → 1707 | 1.25 → 2048 |
| 4K at 100 % | 2.0 → 1920 | 2.0 → 1920 |
| 1080p at 150 % | 1.0 → 1280 | 1.0 → 1280 |
| 1440p at 150 % | 1.0 → 1707 | 1.0 → 1707 |
| 4K at 150 % | 1.5 → 1707 | 1.25 → 2048 |

## 3. Board inventory (the plan read stale copies)

| Canvas | Plan rev 6 said | Final folder | Breakpoints board |
|---|---|---|---|
| Classic | 70 files; no `C-Breakpoints`, no page Huge, no 4K | **165**: 41 Compact, 29 Small, 33 Huge, 5 Huge 4K (Home, Album, Artist, Stage, StageFaces); state boards Compact only, Skeletons C/H | `C-Breakpoints`: Compact 300-527, Small 528-1007, Standard 1008-1919, Huge ≥ 1920 ("Huge 4K uses the same rules at 3840"); "528, not 640"; 40 DIP holds (see hyst above); Short < 680, left at ≥ 720 |
| Zune | 161 | **177** (adds Frame/Artist/NowPlaying Huge 4K, NowPlaying C/S/H, state Compacts, …) | identical |
| Panorama | 82 + 13 pages being drawn | **142**, the 13 pages drawn | identical |
| WinUI | 158 | 158 (the `canvas-v2` copy is final) | `W-Tiers` |
| Short (800 × 330) | being drawn | **none on disk** in any theme; no `C-Shell-Short` file exists (the brief expected one) | – |

The corrections are in §3.1, §3.2, §3.5, §3.6, §2.2, §9.1 R27, §9.2 and §10.2 (A.6 #1-#5, #11). **NQ11 is
withdrawn.** The only design gate left is the four short boards.

## 4. NQ10: confirmed for 16:9 monitors, with one exception

For a 16:9 window, W/1600 = H/900, so `Suggest` ≈ W/1600 snapped down, capped at 2.0 from W ≥ 3200 DIP. The layout
width is then about 1600-2133 below 3200 DIP, and W/2 above it. Reaching 3200 needs W ≥ 6400 DIP. 4K at 100 %: 2.0 →
1920 (Huge). With Manual 100 %, 4K lays out at 3840 (Huge 4K).

The exception is a 32:9 super-ultrawide, where height binds:
- 3840 × 1080 at 100 %: zoom 1.0 → layout 3840 (Huge 4K);
- 5120 × 1440 at 100 %: 1.5 → 3413 (Huge 4K).

For comparison, 3440 × 1440 at 100 % gets 1.5 → 2293 (Huge). The NQ10 wording and §3.2 were corrected (A.6 #6); the
default still stands.

## 5. Design items (not edited)

- **D1**: the pending-tier wake (§1). Fold it into the E22 text before EP22 is planned.
- **D2**: when a settled resize also changes the zoom, the old tier is presented for two frames and the text-scale
  step snaps on the second. Getting to one frame would need the zoom applied before the settle frame's `EnsureSize`
  (an engine pre-paint hook). Default: accept two frames.
- **D3**: Classic Huge docks two panels. TC2's layout record needs a pane-count rung like Zune's `pane.second`; this
  is not a model change.
- **NQ12** (new, in §10.2): the Classic widening holds make 528-567 and 1008-1047 depend on direction (a fresh launch
  at 540 is Small, a window widened to 540 stays Compact). Default: follow the board, fresh resolve by At.
- **NQ13** (new): `C-Breakpoints` still carries the superseded NQ1 zoom rule and "re-evaluate on a 120 ms pause"
  during a drag, which cannot happen on a composited window. Default: the owner's revised NQ1 and §3.3 rule 4 win; TC1
  lists both as conflicts.
- NQ9 (unchanged) and the four short boards remain open.
