# Detail-shell resize flicker — root-cause analysis + fix proposal

**Status: FIXED (2026-07-03).** Canonical doc for the "when I resize the app on detail
shells, things flicker" report (user GIF, 2026-07-03). Everything below is code-verified and
reproduced on-device; file:line references are to the working tree as of 2026-07-03.

---

## 1. Symptom

Resizing the window on a detail page (album/playlist) makes the track list — and occasionally the
right rail and caption row — **blink out entirely and refill** as the drag crosses certain widths.
Reproduced deterministically without user input:

- Launch `Wavee.exe --fake` (+ `WAVEE_LYRICS_OPEN=1`), open any detail page.
- `SendMessage(WM_ENTERSIZEMOVE)` → stepped `SetWindowPos` widths 1620↔1140 (~150 ms/step) →
  `WM_EXITSIZEMOVE`, capturing the DWM composite (`Graphics.CopyFromScreen`) at +50 ms and +150 ms
  per step.
- Result: captures with the **entire track list blank** (header row drawn, all rows missing),
  recovering ~100 ms later. Incidence over identical 16-step storms: **async (default) 8/32
  captures blank, `FG_RENDER_ASYNC=0` 3/32** — happens in BOTH modes; async only amplifies.
- `FG_RESIZE_DIAG=1` smoking gun: on a `resized=True` tick the scene node count collapses
  **1706 → 891 → 735**, then staircases back **839 → 891** over following ticks — every
  intermediate frame is submitted and presented.

Two GIF artifacts are explicitly **not** app bugs: the coherent whole-chrome "backdrop snap"
(~0.3–1 s apart, then settles) is the **DWM OS-Mica sheet re-deriving** after resize (window uses
`DWMSBT_TABBEDWINDOW` + sheet-of-glass; chrome fills are translucent over it — `Win32Theme.ApplyWindowMaterial`),
and the garbled top-edge strip is the recording region including a sliver of the window behind
(reproduced identically in our own captures).

## 2. Root cause — a four-link chain

The blank flash is a deliberate-feature × deliberate-feature interaction, plus a starvation gate:

**Link 1 — tier-keyed remount (app).** `TrackList` derives a column *tier* from its measured
right-area width — `TierFor(w)`: breakpoints **860 / 720 / 560 / 440 / 340** DIP
(`src/apps/Wavee/Features/Detail/DetailTracks.cs:164`). The tier signal is written from
`OnBoundsChanged` (DetailTracks.cs:346) and the tier is **part of the list wrapper's `Key`**
(`"list:t" + tier + …"`, DetailTracks.cs:337). A drag that crosses a breakpoint therefore
**remounts the whole `ItemsView`** — all ~30 realized bound-row slots unmount in one reconcile.
This is by design ("a fresh mount per tier makes the varying column arity recycle-safe"); the
designer priced it as "the rare-resize cost" assuming the remount is visually atomic. It isn't —
see links 2–4.

**Link 2 — cold-mount stagger runs naked (app+engine).** The fresh `ItemsView` mounts with
`staggerColdRealize: true` (DetailTracks.cs:306). The engine then realizes **at most
`ColdRealizeRowsPerFrame = 4` rows per frame** (`Reconciler.RealizeBoundWindow`,
src/FluentGpu.Engine/Reconciler/Reconciler.cs:1282–1288, `entry.Warming`). The stagger exists to
flatten the *navigation* cold-mount spike, where `Skel.Region`'s shimmer masks the fill-in
(DetailTracks.cs:327). On a **tier remount the model is already Ready → no shimmer** — the old
rows vanish in one frame and the new ones fill in 0 → 4 → 8 → … in plain sight. This is the
735 → 839 → 891 node staircase.

**Link 3 — the stagger defeats the same-frame realize guarantee (engine).** The host's D1
realize-after-layout loop (`AppHost.Paint`, src/FluentGpu.Engine/Hosting/AppHost.cs:1343) exists
precisely so "the FIRST presented frame already shows the real rows". The warming cap rolls the
remainder to later frames (`if (entry.LastGrowEpoch == FrameEpoch) target = slots.Count;`,
Reconciler.cs:1286), so the D1 loop cannot complete the window and the partial frame **is
presented**.

**Link 4 — mid-drag frame starvation stretches the blank (engine).** During the OS modal
move/size loop, keep-alive paints bail early when only ambient animation is live:
`if (keepAlive && … && (reasons == None || (_window.InModalLoop && AnimIsAmbient()))) return LastStats;`
(AppHost.cs:1254–1257 — the fix for the "564 redundant present-only paints" seek-ticker waste).
`ComputeWakeReasons()` *does* report warming lists (`HasWarmingVirtuals → FrameNeeded`,
AppHost.cs:603), but the modal-loop arm **ignores the computed reasons** — with music playing,
`AnimIsAmbient()` is true (perpetual seek ticker), every keep-alive frame between drag steps
bails, and the 4-rows/frame refill **stalls until the next WM_SIZE or WM_EXITSIZEMOVE**. That is
why blanks persist 150 ms+ and why the user (who plays music while resizing) sees it so strongly.

**Amplifier (not a cause):** `FG_RENDER_ASYNC` (default ON) roughly doubles observed incidence
(8/32 vs 3/32) — more presented frames land inside the blank window. Reproduced with async OFF, so
the seam is exonerated as the root cause.

## 3. Fix plan

Ordered; fixes 1 and 2 are the shipping fix, 3 is a gate so it never regresses.

### Fix 1 (app) — don't stagger a remount that replaces a live list

Stagger only the *first* (cold, skeleton-masked) mount of a given scroll context; realize tier /
density / filter remounts in full in their mount frame — the D1 loop then makes the swap visually
atomic (old rows and new rows swap within one presented frame).

- `TrackList` keeps a `bool _listRealizedOnce` (reset when the route / `_resetEpoch` changes, i.e.
  when `scrollKey` changes — those are genuine cold mounts, and skeleton/crossfade cover them).
- Pass `staggerColdRealize: !_listRealizedOnce` at DetailTracks.cs:306; set the flag from the
  first Ready-model render.
- Cost audit: a tier remount realizes only the visible window (~20–40 bound slots), not the 10k
  item count — one-frame reconcile of ~30 rows is well under a 120 Hz frame at the measured
  per-row cost; this is exactly the "rare-resize cost" the tier design already accepted.
- Acceptance detail: verify no per-row *enter* transition replays on the tier remount (the keyed
  remount replays mount entrances by design for the `_resetEpoch` crossfade). If rows fade in on a
  breakpoint cross, suppress the entrance for tier-keyed remounts (entrances stay for reset-epoch
  remounts, which want the crossfade narration).

### Fix 2 (engine) — the modal-loop keep-alive skip must not swallow warming work

Narrow AppHost.cs:1254–1257 so the in-modal bail fires only when ambient animation is the *only*
wake reason, instead of ignoring reasons outright:

```
var reasons = ComputeWakeReasons();
if (keepAlive && !resized && _everLaidOut && !_needFullLayout
    && _uiPosts.IsEmpty && !_scene.AnyLayoutDirty
    && (reasons == WakeReasons.None
        || (_window.InModalLoop && AnimIsAmbient() && OnlyAmbientReasons(reasons))))
    return LastStats;
```

where `OnlyAmbientReasons` masks off the reasons that legitimately demand a frame mid-drag —
at minimum `FrameNeeded` (warming virtuals). This preserves the seek-ticker redundant-paint fix
(its frames still have no non-ambient reason) while letting a warming list finish refilling
between drag steps. Standalone value: protects every staggered list (Library, Home shelves)
mid-drag, independent of Fix 1.

- Contract note: this touches the frame-loop keep-alive behavior described in the threading /
  validation design docs — reconcile the owning `design/` doc if its prose states the old
  condition, and run `docs\design\check-canon.ps1`.

### Fix 3 (validation) — blank-frame gate

Add a VerticalSlice gate: headless bound `ItemsView` with `staggerColdRealize:true`, model Ready,
realized window ≥ N rows → flip the wrapper key (simulated tier cross) → run the host frame →
assert the presented frame's realized-row count for the viewport is the full window (Fix 1
behavior), and separately that a warming viewport under `InModalLoop` keep-alives still grows
(`HasWarmingVirtuals` census reaches 0 within ⌈w/4⌉ frames — Fix 2 behavior). Keeps both fixes
from regressing silently.

### Explicitly out of scope

- The DWM Mica backdrop snap after resize (OS-side material re-derivation; not app-drawn).
- Deferring tier flips to `WM_EXITSIZEMOVE`: rejected — live column adaptation during the drag is
  the desired WinUI-like behavior, and with Fix 1 the flip is visually atomic.
- The async seam: exonerated as root cause; no change.

## 4. Verification

1. `dotnet build src/FluentGpu.slnx` clean; `dotnet run --project src/FluentGpu.VerticalSlice`
   → "ALL CHECKS PASSED" including the new Fix-3 gates; app test suite green.
2. On-device re-run of the storm-capture recipe (see memory `detail-resize-flicker-diagnosis`):
   16-step storm on a detail page, +50/+150 ms composite captures, **with playback active** (the
   Link-4 trigger) — assert 0/32 blank captures in BOTH async and `FG_RENDER_ASYNC=0`, and no
   row-entrance fade storm on breakpoint crosses.
3. Manual: drag-resize an album page across 860/720/560 DIP right-area widths while a track plays —
   columns drop/add with no blink.

## 5. Evidence appendix

- GIF forensics: 126 frames @80 ms; flicker = chrome/backdrop state snaps with a static window
  (post-resize recording); window never resizes in-GIF.
- Storm captures: sync `w12-1260-a` and async `w10-1260-a` show the full blank list (header drawn,
  zero rows); async first-storm `w3-1140-a` additionally blanked the rail + caption glyphs
  (re-check after Fixes 1–2; if it persists, investigate separately — likely the DetailShell
  `ModeFor` 820/660/560 band interacting the same way).
- `FG_RESIZE_DIAG` trace: `resized=True … nodes=891` after steady `nodes=1706`, staircase recovery
  `735 → 839 → 891` at +4 rows/frame (`ColdRealizeRowsPerFrame`).
