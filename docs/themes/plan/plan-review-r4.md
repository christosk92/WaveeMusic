# Review of the themes technical plan, revision 5 (round 4)

Reviewer: Opus 5.5, independent. Date 2026-10-11. Inputs: `technical-plan.md` rev 5 (2,797 lines), `owner-decisions-2026-10-11.md`
(incl. "Revision 4 follow-ups" and "Round-3 review follow-up"), `plan-review-r3.md`, the `canvas-v2` tier boards (snapshot),
`parts\r5\*`. Code checked on `origin/main` WaveeMusic `b665627e`, fluent-gpu `9aff04403` (read-only: `git show`/`git grep`).

Status: complete. TPn = line n of `technical-plan.md` rev 5. Three Opus helpers checked the H0 host, the resize path and
the boards; the reviewer re-checked every blocker and the key major claims in the code or on the boards.

Severity: **Blocker** must change before owner approval; **Major** must change in rev 6; **Minor** fix when convenient.

## Summary

| Severity | Count |
|---|---|
| Blocker | 1 |
| Major | 12 |
| Minor | 16 |

**Blocker.**
- **R4-B1.** The tier deferral is not engine-free. A tier signal written in the layout phase of a resized frame is
  flushed **in that same frame** by the global `_runtime.Flush()` inside `FlushRebindsToQuiescence`
  (`AppHost.cs:7197-7210`). That happens after layout effects whenever a scroll plan moved and virtual lists re-realize
  (`:6398-6400`), which is what a resize causes, and again in the flow pass (`:7716`). It is also flushed at `:6319` of the
  next frame even when that frame is resized too. In both cases the crossing snaps. §3.3 rules 2-3, E22 and "E23 deleted,
  no engine change" (TP682) do not hold, and the planned `TierCrossingTests` case "two resized frames in a row" would fail.

**Top majors.** The zoom clamp makes Auto zoom non-monotonic and puts the boards out of reach (R4-M-zoom-a/b). The zoom
"tier hold" shows a clipped old tier for 500 ms (R4-M-zoom-c). H0's `BootCore` takes in the single-instance gate,
`InstallMarshallers`, `Shell.Run` and an unscoped `Platform.Boot`, and a once-per-process boot leaks into the 15k-test
process, where a Debug assert aborts the testhost (R4-M-h0a/b). Hysteresis direction, Zune's second pane, the 1000 edge and
the 330-tall content are not what the boards say (R4-M-hyst/pane/panel/short). "BD-P complete" can never be met
(R4-M-pt2).

**Round 3:** R3-B1 is closed in substance: H0 is feasible with public test API only, and LP1 can be written on main right
after H0. The corrections are majors. R3-B2 is closed in direction but not in mechanism (R4-B1). R3-M1 to R3-M8 are closed,
with the residues listed below. All 14 round-3 minors are applied; minors 1, 2 and 11 were spot-checked and are correct.

**Verdict: needs revision 6.** The blocker is local: about 20 engine lines plus two tests. Everything else is a
correction of numbers, gates or test scope. No owner decision is misapplied.

## Blockers

### R4-B1. A layout-phase tier write snaps on common paths; the deferral needs a small engine commit step
**Plan.** TP631-640 (§3.3 rules 2-3), TP670-683 (E22 `UseTier` "writes it in the layout phase"; E23 deleted, "no engine
change is needed"), TP676 (test "two resized frames in a row both snap and the crossing lands on the first non-resized
one"), TP2303 R11, TP2443 lanes ("no tier PR edits the `AppHost.cs` frame step").

**Code (re-checked by the reviewer, fluent-gpu `9aff04403`, `Hosting\AppHost.cs`).**
- Frame order: `EnsureSize` → `resized` (`:6080`), `PublishViewport` (`:6126`), capture only when `!resized`, else
  `CancelStructuralAll` (`:6180-6198`), drains (`:6222-6237`), `FlushToQuiescence` (`:6319`), layout, then
  `DrainLayoutEffects()` (`:6395`).
- Directly after the layout effects: `if (SyncScrollPlansMidFrame() && … _reconciler.ReRealizeVirtuals()) { if
  (_runtime.HasPending) FlushRebindsToQuiescence(); … _invalidator.RunDirty(layoutSize); … }` (`:6396-6405`).
- `FlushRebindsToQuiescence` is the **global** `_runtime.Flush()` under `PushSuppressBoundTransitions` (`:7197-7210`), not a
  rebind-only flush. A pending tier write is reconciled there, in frame N, with no capture: it snaps. The same call runs
  again at `:7716` (flow pass) and at `:6324`/`:6379` (realize loops, which a bounds-hook source would hit).
- A resize changes the viewport, so the virtual windows move and re-realize: this path is the normal case on any page
  with a virtual list, not an edge case.
- Back-to-back resized frames (Win+Arrow from maximized, Snap assist then snap: two `WM_SIZE` paints,
  `Win32Platform.cs:1954`): the write from frame N is flushed at `:6319` of frame N+1, which is resized too, so it snaps.

**Consequence.** The owner's binding "a tier change never jumps" is not met on the pages that matter most, and the plan's own
gate test fails as written.

**Fix.**
1. E22 gains a small host step (an "E23-lite", ~20 lines in `AppHost.cs`): `UseTier` stores a new tier as a **pending
   value**, not a signal write. The host commits pending tiers at a new step right before capture (`~:6176`), and only on a
   frame that is not resized. That frame then captures, so the crossing FLIPs. Two resized frames in a row both keep
   the old tier.
2. Put this step in Engine B's lane with EP10/EP14 (it edits the frame step), and correct TP2443 and TP682.
3. `TierCrossingTests` adds "resize while scrolled to the end of a virtual list" and "two consecutive resized
   `Paint(keepAlive)` calls, then a non-resized one" (the DPI path, `Win32Platform.cs:1973`).
4. §3.3 rule 4 states the assumption that the window is composited. With Mica off, or if a theme sets `AppOptions.Composited`
   false, every drag tick is resized, and crossings land mid-drag one tick late (still animated with fix 1).

## Findings from the reviewer's own checks

### R4-M-zoom-a (major). The NQ1 clamp against the 2880 reference makes Auto zoom non-monotonic in window width
TP570-578, TP580-593 (table), NQ7 TP2551, C1b TP247.

**Code.** Auto zoom is re-derived on every settled resize, not only on a monitor change: `baseDip` is
`UseDebouncedValue(vp × zoom, 500 ms)` and feeds `FrameRules.AutoZoom` (`Shell\Shell.UI.cs:294-312`, `Shell\Shell.cs:2003-2038`).
`ZoomAutoPolicy`'s documented contract is "a monitor with twice the DIPs must never suggest a SMALLER zoom"
(`Platform\Platform.cs:404-407`), and `Suggest` honours it (`min(W/1600, H/900)`, snapped down, `:431-447`).

**Arithmetic under the plan's rule** ("the largest plateau ≤ Suggest whose zoomed width stays in the reference tier of the
unzoomed width"; reference 640/1008/1920/2880; a window tall enough that width binds):

| Unzoomed width | Suggest | Reference tier | Clamped zoom → layout |
|---|---|---|---|
| 2000-2399 | 1.25 | Huge | **1.0** → 2000-2399 |
| 2400-2879 | 1.5 | Huge | 1.25 → 1920-2303 |
| 2880-3599 | 1.75-2.0 | Huge 4K | **1.0** → 2880-3599 |
| 3600-3840 | 2.0 | Huge 4K | 1.25 → 2880-3072 |

So widening a window from 2879 to 2880 DIP drops the zoom from 1.25 to 1.0, and from 3599 to 3600 raises it again. Each
flip is a 25 % text-size change 500 ms after the resize settles, i.e. a full rescale the user did not ask for. The table
in TP585-591 shows only maximized windows and hides this. It breaks `ZoomAutoPolicy`'s monotonicity contract, which the
plan keeps calling "today's `Suggest`".

**Fix.** Put the behaviour in NQ7 in plain words and offer a monotonic variant as the alternative the plan already names:
"clamp only out of Standard" (for W ≥ 1920: `z = min(Suggest, plateauDown(W / 1920))`; Huge and Huge 4K count as one band
for zoom). That keeps 1440p at 1.25 (2048), gives 4K at 100 % 2.0 (1920) and is monotonic. If the owner keeps the literal
rule, `ZoomTierTests` must pin the flip points and C1b's CHANGELOG must say that Auto zoom can fall when a window grows.

### R4-M-zoom-b (major). The zoomed layouts the clamp produces are not the board sizes, and Zune's Huge 4K becomes unreachable in Auto
TP430-435, TP585-591, TP700-706, NQ7.

- The size brief defines Huge as "2560 × 1440, a 32-inch 1440p screen at 100 %, or a 4K screen at 150 %" and Huge 4K as
  "3840 × 2160, a 32-inch 4K screen at 100 %" (`canvas-v2\SIZES-BRIEF.md:24-25`): the boards assume zoom 1.
- Under the clamp those monitors lay out at 2048 × 1114 (1440p 100 %, 4K 150 %) and 3072 × 1690 (4K 100 %); today they lay
  out at 1707 and 1920. Neither ever reaches the drawn 2560 or 3840 in Auto.
- Zune's Huge 4K starts at 3200 (TP453, TP1819). At 3072 a maximized 4K window at 100 % is Zune **Huge**, so Zune's 4K
  boards (gutter 64, pill 400, play bar 88) are never shown in Auto on the monitor they were drawn for. NQ7's "the Huge
  and Huge 4K layouts become reachable in Auto" is true only for Panorama.
- `TierLayoutTests` (TP696-700) mount 2560 × 1440 and 3840 × 2160 (sizes Auto never produces) and not 2048 × 1114 and
  3072 × 1690 (the sizes most large-monitor users will see).

**Fix.** Tell the owner in NQ7 that the Huge boards are drawn at zoom 1 and that Auto lays them out at 0.8× of their
drawn width; ask whether Zune's 4K threshold should move to the reference 2880 (as Panorama's does) or stay Manual-only.
Add 2048 × 1114 and 3072 × 1690 to `TierLayoutTests` and to the key-surface list.

### R4-M-zoom-c (major). C1b's "tier hold while a rescale is pending" keeps the old tier on screen for at least 500 ms
TP599-603, TP247.

- The rescale is the trailing edge of a 500 ms quiet debounce (`Shell.UI.cs:295-300`, `Shell.cs:2003`). `ZoomService.Pending`
  is not defined in the plan. If it means "the debounce is armed", it is true after **every** resize (maximize, Snap,
  mouse-up of a drag), so every tier crossing waits 500 ms. If it means "the suggestion differs", it is true exactly on a
  monitor or DPI change.
- Either way, for those 500 ms the resized frame's state persists: the old tier's layout at the new size, clipped when
  shrinking (rule 2, TP631-635, says that frame is "presented for one refresh") or small in a big window when growing.
  That is ~60 frames at 120 Hz of a layout the plan itself calls a stopgap, which is the "layout jump" the owner forbids
  in a slower form.

**Fix.** Define `Pending` as "the settled suggestion differs from the live zoom", and do not wait for the debounce on
discrete settled sizes: `WM_EXITSIZEMOVE`, maximize/restore/Snap and `WM_DPICHANGED` already deliver a final size, so C1b
computes the Auto zoom synchronously on that resized frame and writes zoom and tier in the same layout-phase step (the next
frame then crosses once, animated). Keep the 500 ms debounce only for streams of non-modal sizes. Add a `ZoomTierTests`
case: a monitor change shows the old tier for exactly one presented frame.

### R4-m (minor). M14: the Settings slider seed at `Settings.UI.Appearance.cs:86` still bypasses the accessor
TP350. The mechanism says the accessor becomes "the one interpreter of stored values" and re-seeds `:47` and
`Lyrics.UI.cs:315`, but `SeedAppearance` reads the raw key and calls `Resolve` (`Settings.UI.Appearance.cs:86`). With the
Auto arm gone, a stored −1 seeds the slider at `Clamp(−1) = 0` while the lyrics paint at 100. `:86` is in the file list
but not in the mechanism. `:732` (raw compare before writing) is harmless. Fix: name `:86` in step (4) and read it from
`Prefs.Lyrics.BlurStrength()`; add a settings-seed case for stored −1. Otherwise R3-M1 is closed: `Prefs.cs:217-237`,
`Lyrics.cs:175-191, 1425`, `Verse.UI.cs:240` and both tests are covered.

### R4-m (minor). C1's CHANGELOG line contradicts §3.2
TP416 says C1 ships "no 500 ms re-zoom", §7.2 row 17 (TP2194) lists "the 500 ms re-zoom" as removed, but TP597-603 keeps
the 500 ms debounce and builds C1b on it. Presumably C1 removes only the startup re-zoom (`ZoomService.InitialZoom`,
TP1318). Say so, and make C1b update `InitialZoom` too, or the first frame on a large monitor uses the unclamped value and
re-zooms 500 ms later.

### R4-m (minor). The 330 DIP arithmetic is right; NQ9 is well posed
Checked: `ClientToWindowPx` adds `SM_CXPADDEDBORDER + SM_CYSIZEFRAME` to the bottom only (`Win32Platform.cs:1119-1139`),
8 px at 100 % and 12 px at 150 %; `WM_GETMINMAXINFO` sets `ptMinTrackSize` from it (`:1901-1924`). Work area (1080 − 72) / 2 =
504 px = 336 DIP; 330 → 495 + 12 = 507 px; 328 → 492 + 12 = 504 px. The other quarters (408, 456, 387, 285) are correct.
No defect in the numbers (not counted). Keep NQ9.

### R4-m (minor). The critical-path list does not add up to "about 40 serial PRs"
TP2412-2417 lists about 27 PRs (H0, T1, T2a, T2b, T3, T3b, T4a, T8a-c, T10b1-2, M2, M3/M4, Z3, Z6a, Z6b, Z7, Z8b, ~4 ZT
rounds, PT1, PT2, F2, F3). The phase table (TP306-316) gives about 43 days on the same path. Either the list omits PRs or
the count is padded for lane contention. State which, and say that the design milestones (BD-Z, BD-P, BD-C "about 100
more boards", TP2425-2428) are likely the real bound.

### R4-m (minor). The edge table and the mermaid graph of §9.2 disagree
- Mermaid TP2376: `T5 & T6 & T7 & T8c & T9bc & T10a1 & T10a2 & T10b & T10c1 --> T11`. The table (TP2343) has T11a: T4a,
  T11b: T1b + C13, T11c: C14 + C15.
- Mermaid gives Z9/W9 only `Z456 & EP14`; the table adds M8 and M12/M13 (TP2357).
- The mermaid M2 edge needs T11; the critical path (TP2413-2414) shows T11b "already merged beside it".

Pick one set (the heading says "same edges as §4.0 and §2.3") and regenerate the other from it.

## Boards vs plan (task 3; inventory by a helper, key claims re-checked by the reviewer)

Board line numbers count lines after stripping HTML tags. Full notes: helper scratch `boards-verify.md`.

### R4-M-hyst (major). Hysteresis has a direction on the boards; the model stores one number
TP451-454, TP541, TP548, TP1906 (row 40), TP691-697.
- WinUI (`W-Tiers` l.19) and Zune (`Z-Sizes-Breakpoints` l.4: "widening commits at the line, narrowing holds 24 dip below
  it", re-checked) hold on narrowing only. Panorama (`ZF-Breakpoints` l.92) switches 16 past each value and back 16 before
  it: symmetric. Classic's designer brief uses 40 (`classic-plan\PLAN.md` l.66-69); the plan's "24" for Classic is the
  gutter ladder only (C Main l.88).
- `structure.tierHysteresis` and `NamedRung.Hysteresis` are one float, so the direction is lost. With symmetric ±16 a
  window maximized to 1920 from below stays Standard (Huge needs 1936), but a fresh launch at 1920 is Huge. That
  contradicts the first-frame rule (TP594) and the "threshold + 0" edge sizes (TP697).
- Fix: each ladder edge carries `(WidenOffset, NarrowOffset)`; `structure.tierHysteresis` becomes two parameters or a
  record. Correct the Classic row (TP450) and the Classic test values (TP691). `TierParam.FromMetrics` (TP1947) is also a
  new kind of option value (a formula over metrics): name it in §5.3 and say how WVTH011's "≥ 2 values" applies.

### R4-M-pane (major). Zune docks a second pane at Huge; the plan says "none"
TP542, TP1819, TP1907 (row 41). `Z-Sizes-Breakpoints` l.40 (re-checked): the right panel is "docked 720: queue | lyrics ·
friends · 4k docked 1080: queue | lyrics | friends". So Zune's `secondPaneFrom` default is its Huge edge (1920), plus a third
pane at 3200; Panorama's 2400 is an override of a real Zune rung, not a new one. Fix: Zune defaults `pane.second = 1920`,
`pane.third = 3200`; row 41 becomes "2400 vs 1920".

### R4-M-panel (major). Row 38's "1000 is not a free number" has no board behind it
TP543-545, TP1737-1739, TP1873 (row 7), TP1904 (row 38). `W-Tiers` docks a pane when nav + 340 + 480 fits (l.139; 868 at
Small, 1100 at Standard, l.123), with nav thresholds 641/1008. Zune overlays the panel at Small and goes inline only from
1008 (`Z-Sizes-Breakpoints` l.40). `MinPageColumn + 340 = 1000` is the plan's formula, not either board's. Fix: keep
Panorama's tier edge at 1008 (aligned with the reference ladder) and model its 1000 as a `NamedRung` `panel.inline`; or
cite the board that derives 1000. Also fix the WinUI nav rule at TP1737-1739 against `W-Tiers`.

### R4-M-short (major). No board covers the 330 DIP minimum height, and "210 DIP of content" holds for Classic only
TP612-614, TP701. No board in any theme is drawn shorter than 600 tall. Content left at 330 tall from each theme's own
chrome: Classic 48 + 72 → 210 (226 with the 56 mini bar); WinUI 88 + 72 → 170; Zune 48 + 84 + 72 → 126; Panorama ≈ 220 →
110 (130 with its mini bar below 520 tall). Panorama's frame note assumes at least 260 DIP of page (≈ 460-tall window).
`TierLayoutTests` at 330 tall therefore have no design to assert against in three themes. Fix: compute the Short floor per
structure from its own chrome; ask each theme for 800 × 330 and 400 × 330 boards (or a written height rule) before the
330-tall cases are written, and gate those cases on them like the other tier PRs. Tell the owner in NQ9's context that 330
leaves 110-170 DIP of page in the non-Classic themes.

### R4-M-pt2 (major). "BD-P complete" can never be met: the Panorama canvas is finished without twelve brief pages
TP2430, TP2794, PT2 edge TP2361. The folder has 82 boards (81 `ZF-*` + Main; 88 files including variants, re-checked).
Missing at every size: Browse category, Discography, Playlist (followed), Recents, Profile, Show, Episode, Concert, Video,
Setup, Release notes, Feedback; Library has only Albums and Artists. The plan's "thirteen" counts Crash and dialogs (not
brief pages) and misses Playlist (followed). Since the designers call Panorama finished, PT2 → F2 waits on a milestone that
will not arrive. Fix: apply Q18 (undesigned pages built from the theme's parts, reviewed at phase exit): PT2 reviews those
pages against Zune's boards rendered with the WinUI style, and the gate becomes "BD-P as drawn"; or ask the owner for
the boards explicitly.

### R4-M-split (major). The §3.6 inventory and readiness table are already stale, and two cells are wrong
TP728-733, TP740-751. Current counts (Compact / Small / Huge / Huge 4K): WinUI 36 / 27 / 30 / 4 (23 drawn boards not yet in
`canvas.json`), Zune 34 / 34 / 28 / 1, Panorama 19 / 15 / 15 / 4. WT2, WT3, WT4, WT6 and ZT4, ZT5, ZT7 now have their
boards; PT1 is ready (frame C/S/H/4K carry bar and panel tiers). TC3 is "absent" but `C-NowPlaying-Compact` exists. TC10
has no Common-block boards in any theme ("ready" means "no board", so say it is built under Q18). Queue sits in two rows
(WT2 "partial (Queue)" and the stage/lyrics/video/queue row). Fix: since the inventory moves daily, replace the table
with the rule "each tier PR's plan lists its boards from `canvas.json` + disk at plan time", keep only the missing-page
lists, and assign Queue to one family.

### R4-m (minor). Board marks the model does not list
Panorama "Recents into More below 360" (l.30); WinUI content mark 720 (l.154-155; the plan lists 560/860/900/1100);
Short-height lines Classic 680 (`PLAN.md` l.72, TP537 omits it), WinUI 720, Zune 700, Panorama 720 + 520 rung. No board
uses `HeightTier.Tall`: drop it or say what it is for. Classic's C-Frame-Narrow l.49 shows a 640 band over 528-660, so
Classic's Compact edge (528 vs 640) needs confirming against `C-Breakpoints` when it is drawn.

## H0 and LP1 (task 1, R3-B1; helper verification, key items re-checked)

**Feasible as planned with public test API only.** `Shell.Frame()` is public (`Shell.UI.cs:89`). `RootHost`
(`Shell.Host.cs:1041-1070`) only adds root services and then calls `Frame()`. `Sidebar.PaneHost` (internal,
`Sidebar.UI.cs:62`) is reached through the frame mount. The engine inspection is public and works in Release: keys are
recorded with no `#if DEBUG` (`Reconciler.cs:861`, `SceneStore.cs:2586-2590`), and `AppHost.Scene`/`Animation` are at
`AppHost.cs:3122-3123`. The option writers LP1 needs are public (`Prefs.Stage.SetLayout`, `Sidebar.SwitchLayout`
`Sidebar.Host.cs:260`, `Prefs.Appearance.Set`, `Prefs.Lyrics.SetBlurStrength`, `Shell.GoTo`). `Entities.Boot` can run
again (`Entities.cs:1997-2008`). So LP1 can be written on main right after H0, before any view moves. The corrections
below are majors, not blockers.

### R4-M-h0a (major). "`BootCore` extracted from `App.cs:142-231` in the same order" takes in three calls a test must not run, and leaves `Platform.Boot` unscoped
TP220 (H0 prerequisites 1 and 4).
- `App.cs:142-231` (re-checked) starts with `Shell.InstallMarshallers()` (`:142`: one-way, `Shell.Host.cs:361`, and it
  subscribes the static frame tick `:399`), then `Shell.AcquireInstance()` (`:147`: the single-instance gate, which reads
  argv, `Shell.Host.cs:157-170`), and ends with `Shell.Run()` (`:231`). H0's `Boot` excludes "single-instance gate" only for
  the `Run` prelude, not for `BootCore`.
- `Platform.Boot()` runs before that range (`App.cs:88`). It installs the log file, process culture, `Diag.Sink`
  (`Platform.Host.cs:124`: it would route every later engine test's diagnostics into Wavee's log), a crash-bundle
  `UnhandledException` writer (`:149-163`) and `RealtimeCaptureHost.Init`, and it re-parses argv (`Platform.cs:1140`).
  "Memory settings" in H0's `Boot` does not cover these.
- The restorable poster omits seams: `Fetch.WakeForDrain` (`Shell.Host.cs:396`), the fetch idle and drain wakes (`:511`,
  `:522-528`) and `Diagnostics.StoreShot.ToUi` (`:375`). Without them a drain requested outside a rendered frame never
  runs.
- Fix: `BootProfile.HeadlessFake` skips `InstallMarshallers`, `AcquireInstance` and `Run`. Add `Platform.Boot(argv,
  profile)` with crash writers, `Diag.Sink`, culture and capture gated. The headless poster sets and restores all nine
  seams (`Shell.Host.cs:368-396`). Also route the other direct argv reads through `Args.Use`: `Diagnostics.UI.cs:63, 75`,
  `Update.Host.cs:491`, `Platform.Host.cs:130`.

### R4-M-h0b (major). A once-per-process boot inside `Wavee.Tests` leaks into ~15k other tests, and a Debug assert aborts the testhost
TP220 ("boot once, one serialized collection, reset between mounts").
- Process statics survive the boot: the page table, AppActions, the Modules latch (`Modules.Host.cs:59`), `Args.Fake`, the
  settings backing, `UiThreadId`. `BootOrderingTests.cs:26-33` already snapshots and restores the page table for this
  reason.
- `Settings.Set` asserts `UiThreadId == CurrentManagedThreadId` under `#if DEBUG` (`Platform.cs:972-977`, re-checked). An
  xUnit test on another thread fails that `Debug.Assert`, and the default listener tears down the testhost: the whole
  Debug run dies, not one test.
- Timers keep writing after a test ends: Modules' idle timer (`Modules.Host.cs:77`), the History/Session debounces,
  `Playback.Boot`'s ticker (`Playback.Host.cs:340`), `WireMaterialPolicy`'s power-session subscription (`App.cs:46-58`).
  None of them is in the gated list. `Sidebar.Activate` cannot be restored (`Sidebar.Host.cs:139-148`) and posts into a
  dead host after dispose.
- Fix: put H0's harness and every suite on it (LP1, `ViewHarness`, `TierLayoutTests`, `TierResizeTests`,
  `SwitchResetsStateTests`, `RootScopeTests`) in their own test project, e.g. `Wavee.ShellTests` (own process, ~50 lines of
  setup), added to `Wavee.slnx` and to the CLAUDE.md test commands. Add the missing services to `ShellHostOptions`, and
  reset `Sidebar.Activate` to an inline poster on dispose.

### R4-M-lp1 (major). LP1's lyrics-blur pin needs something playing
TP208. The temp profile restores no track. The blur is applied as `BlurSigma` on line paints that follow the playhead
(`Lyrics.UI.cs:1261-1290`). "The mounted lyrics view's resolved strength" is therefore never observed on a painted line.
A fake play runs the real silent audio graph on its own thread (`Playback.Audio.cs:900-906`). Fix: the fixture starts a
fake play with a manual clock, stops audio on dispose, and asserts `Lyrics.Prefs.BlurStrength(onStage)` (`Lyrics.cs:1425`)
plus one line paint's sigma.

## Minors (numbered list; the first five are written out above)

1. **M14**: `Settings.UI.Appearance.cs:86` seed bypasses the accessor.
2. **C1 CHANGELOG**: "no 500 ms re-zoom" vs §3.2; C1b must also update `InitialZoom`.
3. **Critical path**: the listed chain is ~27 PRs, not ~40.
4. **§9.2 table vs mermaid**.
5. **Board marks not in the model**; `HeightTier.Tall` is unused by every board.
6. **LP1 washes (TP208).** The `shell.material.tint` fade is a scene brush animation (`Reconciler.cs:5170-5176`), which
   `HasTracks`/`TryGetTransition` do not see; use `SceneStore.TryGetBrushAnim` (`:999`). The `shell.wash.*` nodes are keyed
   by artwork and fade via Enter/Exit (`Shell.Masthead.UI.cs:189-210`), so assert Enter tracks by prefix, not a brush
   transition.
7. **LP1 `[`/`]` (TP208).** They step the stage layout only in Visualizer mode with no modifier held (`Stage.UI.cs:509-517`).
   `Stage.cs:55-59` are face-step constants, and the popover writer is `Stage.Options.UI.cs:318`. Set
   `Prefs.Stage.SetMode(Visualizer)` first and send the keys through `HeadlessWindow.QueueInput` (`HeadlessPlatform.cs:112`).
8. **C0 scope (TP220).** `IAppSettings` has 6 production implementors (`FileAppSettings` `Platform.Host.cs:417`,
   `RegistryAppSettings` `:488`, `DefaultsOnlySettings` `Platform.Settings.cs:109`, the facade `Platform.cs:968`,
   `PostedSettings` `Update.Host.cs:698`, `OverlaySettings` `Diagnostics.Headless.cs:881`, whose `Remove` needs tombstones)
   plus 4 test doubles, not "three backings". Still about 400 lines.
9. **H0 size.** With R4-M-h0a/b, H0 is ~1.0-1.2k lines plus ~400 for C0: L, under the cap. Say so in TP220.
10. **"Clipped, never squeezed" (TP633-635).** There is no API for the "clip edges" a tier layout record declares. Name it in
    T3b (per-region minimum sizes plus a clipping ancestor) and add a `TierLayoutTests` assertion.
11. **Rule 8 (TP653-655).** The precedent is `EvaluateRestoreHold(resized)` (`AppHost.cs:1071-1077`, used `:6730`). State that
    the cut ends the E17a snap scope before the crossing frame, and add a `TierCrossingTests` case for a resize during a
    freeze. During a composited drag the cut (or the 2 s watchdog) lands at mouse-up.
12. **Citation.** `AppHost.Animation` is `AppHost.cs:3122`, not 3123 (TP208, TP682).
13. **T3b and EP22 sizes (TP246, TP670).** T3b is marked M, but it carries `TierFacts`, `TierPolicy`, `WindowTiers`, the
    content estimate, six ladders moved with their readers re-wired, WVTH013 with analyzer tests and a baseline,
    `TierPolicyTests` and `TierResizeTests`: that is L and near the cap, on the critical path. Move WVTH013 into its own S PR
    after T3b. EP22 (four suites + a VerticalSlice suite + R4-B1's step) is L, not M.
14. **`canvas.json` vs disk.** WinUI has 23 drawn boards not in `canvas.json`; Classic lists 95 boards that are not drawn.
    The orchestrator must read both.
15. **NQ7 wording.** It says "Text gets smaller there". Give the physical effect: 4K at 100 % goes from 2.0 to 1.25, so
    text is 37.5 % smaller. Add the non-monotonic flips (R4-M-zoom-a).
16. **Classic hysteresis/edges (TP450, TP691).** The Classic row says 24 (that is the gutter ladder) and Compact < 528. The
    Classic designer brief says 40, and `C-Frame-Narrow` shows a 640 band. Mark the Classic row "to confirm against
    `C-Breakpoints`" instead of stating values the board does not have yet.

## Round-3 closure (task 1)

| R3 item | Status in rev 5 | Evidence / residue |
|---|---|---|
| B1 headless host | **closed** (feasible, public API only) | residues R4-M-h0a, R4-M-h0b, R4-M-lp1, minors 6-9 |
| B2 tier-crossing animation | **direction right, mechanism incomplete** | R4-B1. Confirmed: zoom is folded into `Scale` (`Win32Platform.cs:864-871`), so a zoom change is a resized frame; mouse-up paints a resized settle frame (`:2024, 2042`) and `_frameNeeded` (`AppHost.cs:7169-7170`) produces N+1 promptly; live video paints but keeps the old layout size (`:8289-8301`). Maximize/Snap/restore/DPI/monitor follow the same two frames once R4-B1 is fixed. |
| M1 −1 → 100 | closed | `Prefs.cs:217-237`, `Lyrics.cs:175-191, 1425`, `Verse.UI.cs:240`, tests; residue minor 1 (`Settings.UI.Appearance.cs:86`) |
| M2 zoom ladder | closed as sequencing (C1b after T3b, core ladder) | table numbers check out (`Platform.cs:425-447`): 1.5→1.25 (2048), 2.0→1.25 (3072), 1.5→1.25 (2048). New problems: R4-M-zoom-a/b/c |
| M3 tier model | partly | Huge4K, optional edges, `NamedRung` fine; hysteresis direction, Zune second pane, 1000 edge wrong (R4-M-hyst/pane/panel) |
| M4 WVTH013 | closed | syntax rule (a)/(b)/(c) with analyzer tests (TP378-383) |
| M5 tier PR split | closed in structure | inventory stale, Panorama gate unreachable (R4-M-split, R4-M-pt2) |
| M6 graph | closed | per-PR edges present; minors 3-4 |
| M7 330 DIP | closed | arithmetic verified (`Win32Platform.cs:1119-1139, 1901-1924`); residue R4-M-short (content height per theme) |
| M8 frame measurement | closed | TP1109, TP1193 (headless no-tracks + WARP readback in the same run) |
| Minors 1-14 | applied | 1 (`Shell.Host.cs:317-332`, `Platform.cs:912-914`), 2 (`Sidebar.UI.cs:84, 163, 860`), 11 (`Browse.Rules.cs:67-68`) verified |

## New problems from rev 5 (task 2)

- R4-B1 reintroduces an `AppHost.cs` frame-step edit, so the lane note at TP2443 is wrong again. Serialize EP22 with Engine B.
- The dependency graph has no cycles. H0 → LP1 → C-PRs is coherent, and "H0 lands before C1 and A1" (TP2444) holds.
  The ~50-day estimate is plausible for the PR chain, but the design milestones (BD-Z Now Playing and states, BD-C
  ~100 boards, the Panorama pages) are the real bound (R4-M-pt2, minor 3).
- PR count 175 is consistent with the phase totals (TP290-296). Moving H0's suites to their own test project and
  splitting out WVTH013 add 0-1 PR.
- **R2-B1** stays moot (Q26). **R2-B2**: closed, with a stated measurement (R3-M8). **R2-B3**: unchanged and closed. Note
  that `CaptureBgra` reads the back buffer, not the DWM composite (`AppHost.cs:1586`), so the Mica part stays on
  `DwmOrderTests`, as planned. **R2-B4**: closed once R4-M-h0a/b and R4-M-lp1 are applied; LP1 can then run on main
  before any view moves.

## NQ7-NQ9 against the owner rules (task 4)

- **NQ7** (default: apply NQ1 with the 2880 reference). No `if (theme == X)` and no theme-dependent zoom, so no rule
  conflict in kind. But the default (a) makes Auto zoom fall when a window grows (R4-M-zoom-a), which rescales on resize:
  close to "no layout jumps". It also (b) never reaches the drawn board sizes and hides Zune's 4K boards (R4-M-zoom-b).
  The owner must see both before answering. A monotonic alternative is "clamp only out of Standard". It satisfies NQ1's
  intent except that 4K at 100 % stays at 2.0 (1920), so Huge 4K boards are reachable only in Manual or on monitors wider
  than 3840.
- **NQ8** (the canvas wins over #205-era decisions). Consistent with "PR #205 discarded" and with the decisions file's
  "assumed … not confirmed". No conflict.
- **NQ9** (keep 330, fall back to 328 if Snap counts the border). The numbers are right. The fallback changes a binding
  owner number, so asking is correct. Add that at 330 tall the page area is 110-170 DIP in WinUI, Zune and Panorama
  (R4-M-short): the owner chose 330 for Classic-sized chrome.
