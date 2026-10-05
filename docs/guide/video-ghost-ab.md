# Video ghost A/B: clear local file vs Spotify PlayReady

Every video the owner has watched so far was PlayReady (Spotify music and podcast video are both DRM), so "only DRM
ghosts" is a sampling artefact, not evidence about the protected pipeline (audit F257). From the bind onward both paths
share `VideoBinding`, `VideoSurfaceRegistry`, `DCompVideoPresenter` and the DestOut hole; they differ in the source (MF MP4
vs CencMediaSource + PMP) and in who sizes the stream. This is the cheapest experiment that separates the two (F259).
It needs no build flags: a local file attached to a track is the clear arm.

## What you need

- One track that has a Spotify video, and a **1280x720** local `.mp4` of it (re-encode the official video if you have to).
  Match the natural size of the Spotify rung (`natural=1280x720` in `[video] first.frame`). A 1080p file is a different
  experiment: at about 1600x900 the clear file is never upscaled and the DRM one is, so resolution and protection mix.
- A pop-out window you can size exactly (separate window), and the newest Wavee log file to read afterwards.

## Procedure

1. **Attach the clear arm.** Right-click the track, Video, Attach video file, pick the 1280x720 `.mp4`.
2. **Play the track as video in the pop-out** and size the window to about 1600x900 device px. Watch for the ghost (record
   at 240 fps or with PresentMon if you can).
3. **Detach the override** (Video, Remove video) and play the SAME track again in the SAME pop-out size: the Spotify DRM arm.
4. **Shrink the pop-out** until the picture rect is at most 1280x720 and repeat both arms. Start the track again after
   resizing: the mount line is written once per source, at its first frame.

Always restart the track after changing the window size, and keep the same track, same pop-out position and same display.

## Confirm each arm really mounted

The override resolve alone proves nothing: a `tier=override` resolve with `for=warm` is the prefetch keeper looking ahead
(it discards non-DRM sources by design), which is the only override line the 2026-10-01..03 logs contain. For the clear arm
you need this chain for the track:

```
[video] resolve track=... tier=override ... for=playback
[video] open.ok key=local:video:...
[video] first.frame ... natural=1280x720
[video.mount] owner=PopOut placement=Detached tier=override natural=1280x720 content=... dev=(...) scale=...
```

No `for=playback` line means the video was never asked for (the placement stayed None). If the open fails the attachment is
quarantined and the original plays (toast "Couldn't play the attached video"): then the next `[video.mount]` says
`tier=playready`, not `override`.

## Reading the numbers

`[video.mount]` (the app, once per source at its first frame) carries the tier, the natural size, the content size the
stream is worth and `dev`, the device rect of the FITTED picture (the natural size fitted uniformly inside the stage box and
centred; `box=(...)` is the raw stage box). `content` is computed from that same rect, so `content` vs `dev` reads exactly as
in the engine's `[video.surface] place ... dev=(x,y,w,h) content=WxH` line (written when the surface is created or bound).
It is still an estimate under a non-Uniform aspect mode, where the engine's place line is the exact pair.

- `content == dev` (same width and height): the stream is rendered at the rect size and DirectComposition scales by 1.0.
  Nearest-neighbour upscaling cannot be the cause. At 912x513 in the old logs this held (`dev=(-41,0,912,513) content=912x513`).
- `content < dev`: the stream is smaller than its rect and DComp scales it up with the default bitmap interpolation (nearest
  neighbour; nothing in FluentGpu.Windows calls `SetBitmapInterpolationMode`). A 1280x720 stream in a 1600x900 rect is this.

| Observation | Reading |
|---|---|
| Ghost on the clear arm AND the DRM arm | Compositor or panel (hole, visual tear, Mica show-through), shared by both paths. DRM is exonerated. |
| Ghost on both at `content < dev`, gone at `content == dev` | Nearest-neighbour upscale (F073) was the dominant term. |
| Ghost only on DRM, only when `content < dev` | A frozen natural size plus nearest-neighbour in the protected sizing path. |
| Ghost only on DRM, even at `content == dev` | The protected decode or present path: add the frame statistics from F066 next. |

Do not tune PMP or CDM internals before this table has an answer.

## Related log lines

- `[video] resolve ... for=playback|warm`: who asked. `warm` never mounts anything.
- `docked cap fit`: not rate-limited. It follows discrete events (source, decoded size, rail width), so it writes once per
  change. A rail splitter drag writes one at release, because the rail previews on a guide and commits on release (F243).
- `docked slot`: at most one per 250 ms during a sweep (window resize, the vertical docked-height splitter, the commit
  reflow). The settled rect is always written last, 250 ms after the sweep stops, and `held=N` counts the lines dropped in
  between.
