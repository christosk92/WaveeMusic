# Memory while Wavee is hidden

When the main window is minimized, hidden to the tray, or completely covered by another window for long enough, Wavee
releases the GPU memory nothing on screen needs and builds it again on the way back. This page says what is released,
when, how to switch it off, and how to measure it. The engine side is `HiddenMemoryPolicy` (`FluentGpu.Hosting`).

## What is released (the "Shallow" stage)

After the window has been parked for the delay below, the host releases, on the render thread and behind the GPU fences:

- every retained raster tile, visible ones included (the window is not visible, so none is needed);
- the scratch, retained, group and blur surfaces, the stencil target, the blur pyramids, the free texture pools, the
  upload staging ring and the placed-heap warm pages;
- every image texture nothing pins or holds. Images a list row shows (image cells are held but not pinned), the
  pinned covers on screen and anything a pop-out window holds are **kept**;
- the CPU pixel-buffer pool.

Everything is rebuilt by the first frame after the restore: the restore edge forces one full repaint, every tile is
re-rastered in the same submission that composites it, and the images that were released come back when the rows that
show them ask for them again (from the disk cache, a few milliseconds each). A restored window never shows a blank, a
checkerboard or a stale tile. Pinned image textures stay resident at this stage; the Deep stage below releases those.

## The Deep stage

After **5 minutes** minimized or in the tray (never for a window that is only covered), the host also releases the textures
of the images the window's own content holds: the pinned covers on screen, list row images and everything a hidden page still
names. What stays:

- the player bar's now-playing artwork (`ImageEl.KeepWhileHidden`, set by `Controls.Artwork(keepWhileHidden: true)`), so the
  bar is never without its cover;
- derived (blurred) images: they are small, bounded by their own cap, and re-baking them on the way back costs more than
  keeping them;
- whatever a visible pop-out window holds. A pop-out that was itself hidden loses its images with the main window's and gets
  them back, with its own held first frame, when it is shown.

The cache keeps each image's identity (size, "was ready") so nothing re-lays out. Each released image is restarted through the
ordinary decode path on the restore edge, the ones that intersect the window first (on the Visible lane) and the rest after
the first frame. That includes images that are only held (a list row's image cell in overscan, a cover on a page kept alive in
the background): a row cell is requested but never pinned, and nothing asks for it again when its row scrolls into view, so it
must come back with the rest. Images on a page kept alive in the background, or in a collapsed pane, restart too but the held
frame does not wait for them.

### The restore hold

Restoring cannot hide the cost of re-decoding the visible covers, so the first frame back is **held**: it is recorded and
submitted (so the uploads and the glyph atlas land) but not presented, and the window keeps showing the last frame it
presented before the hide, behind the DWM restore animation. It is presented only when it is faithful: the glyph atlas is
whole and every image the user had seen before the hide that the frame names has a texture the GPU can sample. That check runs
on the render thread against the frame it just recorded, so it also covers uploads still queued behind the 2 MiB-per-turn
staging cap (which is lifted while holding) and a stale pre-hide frame that was adopted first.

- The UI holds for at most **200 ms** (`--fg hidden=...:...:...:HOLD`, `0` turns the hold off); `RestoreHoldTimeouts` counts a
  restore whose images were still decoding then. The images stay tracked, so the render side's faithful-frame check keeps the
  old frame on the glass until they are resident: the user sees the old frame, then the right one, never a placeholder in
  between. A render-side guard presents anyway 500 ms after the first frame the UI released (a gate that can never open, such
  as a rejected upload), so a restore never freezes; stragglers after that take the ordinary warm reveal.
- Not armed when the window came back at a different size or DPI (the held frame would be stretched) or when nothing on
  screen was released. A window re-parked mid-hold keeps the hold, and the deadline restarts when it is shown again.
- A held frame is not a covered window: the app-wide occlusion signal (`InputHooks.WindowOccluded`) never flips for it,
  visualizers and meters keep running, video placement only applies releases, and compositor motion is neither paused nor
  re-anchored. The hold rides the published frame (a `PresentHeld` bit), so a release cannot race the frame it governs.

## When

| How the window left the screen | Delay before the release |
|---|---|
| Minimized, or hidden to the tray | 2 s (Shallow), then 5 min (Deep) |
| Only covered by another window (alt-tab away to a maximized window) | 30 s (Shallow only; never Deep) |

A cover is back the instant the other window moves, with no DWM animation to hide a re-raster, and it happens all day, so
it waits much longer. A park shorter than its delay releases nothing at all.

The Deep delay counts from the moment the window became minimized or hidden (a window covered for an hour and then minimized
has not been hidden for an hour). A Deep window that is later only covered (restored under a maximized window) keeps its stage
until it is un-parked; whenever anything is still released at the un-park, whatever the park kind, the restore restarts it and
arms the hold.

`--fg hidden=SHALLOW[:COVER[:DEEP[:HOLD]]]` changes them in milliseconds (`max` = never): `--fg hidden=max:max` turns the release
off, `--fg hidden=0:0` releases on the park edge itself. Terms can be named and given in any order: `--fg hidden=deep=max` turns
off Deep alone, `--fg hidden=hold=0` keeps Deep but never holds the first frame, `--fg hidden=2000:5000:5000:200` reaches Deep
after 5 s hidden. This is how a before/after comparison is run.

## The image budget

Unrelated to hiding but shipped with it: the image cache's cap is now `max(today's, window-derived)` (three window areas of
pixels, clamped to 32 MiB .. 64 MiB weak / 96 MiB discrete; a cap set below 32 MiB is left alone), re-derived when the window
changes size, and the cache charges what the device **measured** a 64/128/256/512 texture to commit (the Adreno commits 320 KiB
for a 256 x 256, the formula said 256 KiB). Both caps scale by the largest measured/formula ratio of any measured bucket (never
below 1) in the same call, so the number of images held, of any size, does not shrink. Pinned images are never evicted by either.

## Measuring it

```powershell
powershell -File ops\tools\hidden-mem.ps1 -Exe <Wavee.exe> -ProfileDir C:\scratch\hm -OutDir C:\scratch\hm-after -Cycles 10
powershell -File ops\tools\hidden-mem.ps1 -Exe <Wavee.exe> -ProfileDir C:\scratch\hm -OutDir C:\scratch\hm-before -Cycles 10 -Fg hidden=max:max
powershell -File ops\tools\hidden-mem.ps1 -Summarize C:\scratch\hm-after -Baseline C:\scratch\hm-before
```

The script runs the `hide-restore` frame-bench scenario (`docs/guide/frame-bench.md`) in a window it starts itself with a scratch
`--fake` profile, never the owner's running Wavee. Per mode (minimize, hide, cover) and phase (vis, hid5, hidEnd, res) it
prints process private MB, working set MB, DXGI LOCAL usage MB, the engine's tracked GPU MB and the image cache MB, the
restore latency (restore call to the first present after the host un-parked; a frame that arrives more than 300 ms later is unrelated and the restore counts as `noPresent`) and the validation counters. `-Validate` adds
`--fg present-validate,damage-validate`; `presentBad`, `damageBad` and `tileBad` must be 0 (the script exits 2 otherwise). A
restore whose frame is legitimately elided as unchanged presents nothing and is counted as `noPresent`, not as a latency.

Reading it: private bytes and the working set fall by less than the tracked GPU bytes, because the driver returns memory in
whole chunks and the OS trims a minimized process's working set on its own after `SW_MINIMIZE` (a tray hide does not, so
read the hide rows for the engine's own effect). The app's heap policy also compacts the GC heap while the window is off
screen; that is in every run, before and after alike.

With 10 cycles a p95 is the second-worst sample: read the max, or run 20 or more. A cycle whose window never parked is left out (and fails the run). `-CoverSec 6 -Fg hidden=2000:5000` exercises the cover release; the default cover cycle (3 s) only proves nothing is released. `hiddenMcPerSec` is the process's CPU in mega-cycles per second (`QueryProcessCycleTime`) while hidden.

The live log carries the stage too: the `mem.sample` line has `hidden=stage:visible|shallow|deep:parked:0|1`, and
each release and restore writes a `[hidden]` line (`[hidden] deep ... parked=N`, `[hidden] restore ... restarted= pending= hold=`,
`[hidden] hold release ms= timeout=`). Deep and the hold are exercised by `hide-restore` with `--fg hidden=2000:30000:5000:200`
(Deep after 5 s hidden); the races between the published hold bit, the render-side latch and the gate only exist in the
default Async loop, so measure there, on the Adreno, with a looping animation on screen.

## Known limits

- A device loss while hidden parks what is held (and drops the rest) instead of re-decoding it for nobody; the restore
  restarts it inside the same held first frame.
- The glyph atlas and the swapchain are not released at Deep (the atlas release is a separate, later piece), and the
  `Evict` / `MakeResident` residency variant is not built: Deep frees by releasing textures.
- Deep ships on in Wavee only because the player-bar artwork is pinned (`keepWhileHidden`); an app that adopts the engine
  without pinning its always-visible art should pass `--fg hidden=deep=max` until it does.
- The hold deadline is 200 ms (about the DWM restore animation); the right number is whatever gives 0 timeouts across 20 Async
  `hide-restore` cycles on the heaviest pages, which has not been measured yet.
- The 144 MB write-combined driver heaps and the swapchain are not released.
