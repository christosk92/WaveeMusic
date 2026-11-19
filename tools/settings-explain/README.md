# Settings explanation clips

The short looping clips behind the explanation badges in Settings (settings rework, DESIGN 6.5). A badge sits beside the
label of a setting whose effect you see or hear; it opens a tip with a 2-4 s loop: the before state, a hold, the change,
a hold on the after state. No text is baked in; the tip's caption is the loc line `settings.explain.<dialId>`.

Every clip is recorded from the real app by `record.py`, which drives the headless inspector (`Wavee.exe --inspect`
through `tools/inspect.py`). Nothing is hand-made: when a dial or a theme changes, record it again.

Clips live in `src/apps/Wavee/assets/settings-explain/<theme>/<mode>/<clipId>.webp`: one per clip, per theme (classic,
winui, zune) and app mode (dark, light). A theme option is recorded only in its own theme. `clipId` is the dial id
(`appearance.connectedAnimations`); `SettingsDial.Explainer` names it, and a clip can serve more than one dial
(`playback.crossfade` also explains Crossfade duration).

## Run it

Build the lane and copy the build (never drive the exe a build writes to; `fg.py orient --root <lane>` prints both):

```text
python -I tools/settings-explain/record.py --exe C:/WAVEE/scratch/<lane>/run/Wavee.exe              # every recordable clip
python -I tools/settings-explain/record.py --exe <exe> --only lyrics.blur playback.crossfade --modes dark
python -I tools/settings-explain/record.py --exe <exe> --only "appearance.option.*" --themes winui
python -I tools/settings-explain/record.py --exe <exe> --out C:/WAVEE/scratch/<lane>/clips             # not into the assets
python -I tools/settings-explain/record.py --list                         # the manifest expanded, with what is pending
python -I tools/settings-explain/record.py --check                        # the committed clips against the budget
python -I tools/settings-explain/record.py --check --json --strict        # for the release validator: a missing clip fails
```

Each run writes a contact sheet per clip to `--work` (default `%TEMP%/wavee-settings-explain/sheets`): every output frame
with its duration. Look at them before committing; `--keep-frames` keeps the captured PNGs too. `--jobs` runs up to four
inspector processes side by side (2 by default). Inspector windows are headless; nothing opens on screen.

Entries marked `pending` are skipped: `part: surface` (the surface is on another branch), `part: inspector` (the headless
inspector cannot stage it yet). `--pending` tries them anyway. `part: dial` (the dial is on another branch) records at the
build's default when the entry says `withoutDial: record`.

## How a clip is made

- One fresh inspector process per clip x theme x mode, mounted at the entry's size and `scale` (2 by default, so a
  320x180 DIP region is 640x360 px).
- The setup lines, then the before value through `setting id=<dial> value=<v>`: the dial's own writer, as a Settings view
  calls it. A rest, then `render` of the before frame.
- The change: the after value written with `settle=false` (a slider is swept through its values; an `after` list steps
  through several values, each held), or the entry's action (a click, a `goto`). Then `frames <step>` + `render` per
  captured frame on the inspector's 16 ms clock until the window holds still (renders of a still scene are identical) or
  `capture` seconds pass.
- The crop: the union of the region keys (`find`, in both states) grown to 16:9, at least `minWidth` DIP wide, kept
  inside `within` and the window. Never the whole window unless the window is the subject (the full screen, the mini
  player).
- The frames are resampled to 15 fps (motion can be slowed with `slow` so a 300 ms flight is readable), cropped, scaled to
  640x360, merged where identical (sub-rectangle frames do the rest), and encoded with libwebp's WebPAnimEncoder through
  Pillow: lossy, quality 70 (an entry may start higher: dim lyrics on a dim ground smear into a blur of their own at 70,
  so `lyrics.blur` starts at 90), mixed frames, one key frame, no alpha. Not ffmpeg: its `libwebp_anim` files decoded
  several times slower in the engine (`ops/release/wavee/README.md`, Motion highlights).
- A still stretch inside the change is kept as it was, unless the entry sets `maxStill` (a marquee waits 2.5 s before
  its pass: that wait is cut to 0.4 s).
- Sound dials (Crossfade, Normalization, Loudness level) are diagrams drawn by the script: two waveforms sliding into a
  crossfade, loud and quiet songs evening out, the level stepping Quiet, Normal, Loud. Their colours are read from the app
  (the Settings page rendered and dumped in that theme and mode: background, ink, quiet ink, accent). No audio, ever.

## Add a clip

1. The dial must exist in the registry (`setting` lists every dial; `Screens/SettingsSpec.*.cs`).
2. Add an entry to `clips.json` (its `_doc` names every field): `clip`, `dial`, `kind` (`state`, `motion` or `diagram`),
   `themes`, the surface (`route`, `mountArgs`, `setup`), `before` and `after` (or `value` and `action`), and the `region`
   (node keys from `find`, or a rect). Any field can differ per theme: `{"winui": ..., "default": ...}`.
3. Find the keys with the inspector: `python -I tools/inspect.py --exe <exe> -c "mount ..." -c "find prefix=<p> visibleOnly=true"`.
4. Record it in one theme and mode, read the contact sheet, then all of them; `--check`.
5. Commit the clips with the change that needs them.

## Budget

At most **120 KB per clip** and **9 MB for the whole folder** (it ships inside every package). The encoder retries at
lower quality (from the entry's quality down to 55 at 15 fps, then 12 fps from 55 down to 25) until a clip fits; `--check` fails on a clip over
120 KB, a total over 9 MB, a file that is not a 640x360 animated WebP, a loop outside 2-4 s or a frame faster than 15 fps,
and warns on a file the manifest does not name or one carrying alpha. With `--strict` a clip the manifest expects and a
non-pending entry lacks is an error too (a badge shows only where its clip exists; the Release build fails on a missing
one).

The manifest expands to 122 clips (`--list`), so the folder can average about 75 KB a clip; the 84 this branch can record
measured about 3.4 MB together (41 KB on average). Most recorded clips run
10-60 KB; the heavy ones are a page swap filmed twice (Fast navigation, about 70-120 KB), the waveform diagram (50-70 KB)
and the spinning player styles (55-80 KB).
