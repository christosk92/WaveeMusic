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
checkerboard or a stale tile. Pinned image textures stay resident; releasing those needs a presentation hold on restore
and is a separate piece of work.

## When

| How the window left the screen | Delay before the release |
|---|---|
| Minimized, or hidden to the tray | 2 s |
| Only covered by another window (alt-tab away to a maximized window) | 30 s |

A cover is back the instant the other window moves, with no DWM animation to hide a re-raster, and it happens all day, so
it waits much longer. A park shorter than its delay releases nothing at all.

`--fg hidden=SHALLOW[:COVER]` changes the delays in milliseconds (`max` = never): `--fg hidden=max:max` turns the release off,
`--fg hidden=0:0` releases on the park edge itself. This is how a before/after comparison is run.

## Measuring it

```powershell
powershell -File ops\tools\hidden-mem.ps1 -Exe <Wavee.exe> -Profile C:\scratch\hm -OutDir C:\scratch\hm-after -Cycles 10
powershell -File ops\tools\hidden-mem.ps1 -Exe <Wavee.exe> -Profile C:\scratch\hm -OutDir C:\scratch\hm-before -Cycles 10 -Fg hidden=max:max
powershell -File ops\tools\hidden-mem.ps1 -Summarize C:\scratch\hm-after -Baseline C:\scratch\hm-before
```

The script runs the `hide-restore` frame-bench scenario (`docs/guide/frame-bench.md`) in a window it starts itself with a scratch
`--fake` profile, never the owner's running Wavee. Per mode (minimize, hide, cover) and phase (vis, hid5, hidEnd, res) it
prints process private MB, working set MB, DXGI LOCAL usage MB, the engine's tracked GPU MB and the image cache MB, the
restore latency (restore call to the first present after it) and the validation counters. `-Validate` adds
`--fg present-validate,damage-validate`; `presentBad`, `damageBad` and `tileBad` must be 0 (the script exits 2 otherwise). A
restore whose frame is legitimately elided as unchanged presents nothing and is counted as `noPresent`, not as a latency.

Reading it: private bytes and the working set fall by less than the tracked GPU bytes, because the driver returns memory in
whole chunks and the OS trims a minimized process's working set on its own after `SW_MINIMIZE` (a tray hide does not, so
read the hide rows for the engine's own effect). The app's heap policy also compacts the GC heap while the window is off
screen; that is in every run, before and after alike.

The live log carries the stage too: the `mem.sample` line has `hidden=stage:visible|shallow:parked:0|1`, and
each release and restore writes a `[hidden]` line.

## Known limits

- A device loss while hidden re-decodes the images that survived the release (as it does while visible); the restored window
  then finds them ready.
- The 144 MB write-combined driver heaps and the swapchain are not released.
