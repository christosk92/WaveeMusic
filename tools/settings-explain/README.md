# Settings explanation clips

The short looping clips behind the explanation badges in Settings (settings rework, DESIGN 6.5). A badge sits beside the
label of a setting whose effect you see or hear; it opens a tip with a 2-4 s loop: the before state, a hold, the change,
a hold on the after state. No text is baked in; the tip's caption is the loc line `settings.explain.<dialId>`.

Every clip is recorded from the real app by `record.py`, which drives the headless inspector (`Wavee.exe --inspect`
through `tools/inspect.py`). Nothing is hand-made: when a dial or a theme changes, record it again.

Clips live in `src/apps/Wavee/assets/settings-explain/<theme>/<mode>/<clipId>.webp`: one per clip, per theme (classic,
winui, zune) and app mode (dark, light). A theme option is recorded only in its own theme. `clipId` is the dial id
(`lyrics.blur`); `SettingsDial.Explainer` names it, and a clip can serve more than one dial (`playback.crossfade` also
explains Crossfade duration).

## Status

A badge is registered (`s_clips` in `src/apps/Wavee/Screens/SettingsExplain.cs`) for a theme only when its clips exist
there in both app modes, so the registry is the committed clip set, exactly. `clips.json` keeps the whole target list of
DESIGN 6.5 with a `status` per entry.

| Dial | Classic | WinUI | Zune | How |
|---|---|---|---|---|
| Fast navigation | yes | yes | (no dial) | two takes, off then on, slowed 2x |
| Liked Songs cover | - | yes | - | the Liked page's cover stepping through styles |
| Navigation pane, Density, Artwork in track rows | | yes | | WinUI options |
| Track list style, Sidebar layout, Marquee text | yes | | | Classic options (Marquee text is an app dial Classic lists) |
| Background | | | yes | Zune option, on Home while a song plays |
| Normalization, Loudness level, Crossfade (+ duration) | yes | yes | yes | drawn diagrams: no audio, ever |
| Count down before the next video | yes | yes | yes | a video ending in full screen with another queued |
| Lyrics blur | yes | yes | yes | the rail's blur swept 0 to 100 (Classic at scale 1: its rail blurs by device pixels, so at scale 2 the change hardly shows) |
| Word-by-word AI lyrics | yes | yes | yes | a line-synced song timed word by word (see below) |
| Player style, its finish | yes | yes | yes | the Now playing stage stepping through styles and finishes |
| Full-screen layout, Spectrum | yes | yes | yes | the stage at 1280x720 in Visualizer mode (the layouts are its; Lyrics mode keeps its own) |
| Mini player face, Colour from art | yes | yes | yes | the mini player window |

Not registered yet, and why:

- **Calm motion**: not recordable headless. Calm tames how the stage answers the music (level, beats, onsets); headless
  there is no audio level, so a take with Calm and one without look alike.
- **Backdrop (WinUI)**: not recordable headless. Mica and Acrylic sample the desktop behind a real window; a headless
  render has none, so every backdrop renders alike. Needs a real-window recording (off-screen) or a stand-in desktop in
  the inspector.
- **Canvas**: not recordable headless on this line. The Canvas player draws no video frames headless (the poster, then
  black); the fake media backend that gives it frames (`mount media=fake`) is on the feat/home-* line.
- **Daylist clock, Previews on Home**: need feat/pinned-home (their surfaces land with the pinned Home).
- **Connected animations (WinUI)**: needs feat/pinned-home (the dial); the entry records there as written.
- **Liked Songs cover in Classic and Zune**: their Liked page draws no cover at the default layout, so there is no cover
  to film.
- **Tinted surfaces (Classic)**: no visible change in light mode. Classic's light theme draws its surfaces untinted (Off,
  Subtle and Rich differ by at most 6 levels on any page and cover grade), so a light clip would show nothing changing;
  the dark one records, but a badge needs both modes (`pending.part: effect`).

Two entries use inspector states made for them: **Word-by-word AI lyrics** mounts the fake lyrics line-synced
(`mount lyrics=line`), and `state kind=ai ai=timed words=on` gives the song a finished job's word timing through the
store upgrade a cached result takes (no aligner runs; each line's words share its span). A view takes such an upgrade at
once only while no line is lit, so the entry seeks to the intro, stages it, and seeks back to the line: the same line
then wipes word by word. **Count down before the next video** plays tr26 as video (`video=fullscreen player=scripted`)
with tr39 queued and ends it with `transport do=end` (the natural end): the stage's end card counts down "Up next in 5".
The picture behind it is the stage's ground (no frames decode offline); the card is the change.

## Run it

Build the lane and copy the build (never drive the exe a build writes to; `fg.py orient --root <lane>` prints both):

```text
python -I tools/settings-explain/record.py --exe C:/WAVEE/scratch/<lane>/run/Wavee.exe              # every recordable clip
python -I tools/settings-explain/record.py --exe <exe> --only lyrics.blur playback.crossfade --modes dark
python -I tools/settings-explain/record.py --exe <exe> --only "appearance.option.*" --themes winui
python -I tools/settings-explain/record.py --exe <exe> --out C:/WAVEE/scratch/<lane>/clips             # not into the assets
python -I tools/settings-explain/record.py --list                         # the manifest expanded, with what is pending
python -I tools/settings-explain/record.py --check                        # the committed clips against the budget
python -I tools/settings-explain/record.py --check --json --strict        # a missing clip of a recordable entry fails
```

Each run writes a contact sheet per clip to `--work` (default `%TEMP%/wavee-settings-explain/sheets`): every output frame
with its duration. Look at them before committing; `--keep-frames` keeps the captured PNGs too. `--jobs` runs up to four
inspector processes side by side (2 by default). Inspector windows are headless; nothing opens on screen.

Entries marked `pending` are skipped: `part: surface` or `part: dial` (the surface or the dial is on another branch),
`part: inspector` (the headless inspector cannot stage it yet), `part: effect` (the app shows no change in one app mode). `--pending` tries them anyway; a missing dial then records
at the build's default when the entry says `withoutDial: record`.

The Release build runs the app's own check (`Wavee.exe --check-explainer-clips <assets/settings-explain>`, MSBuild
property `WaveeExplainerClipCheck`, on by default, target `WaveeCheckExplainerClips`): it fails when a registered badge
lacks a clip for a theme or mode it applies to, or a clip breaks the budget. A Debug build never runs it.

## How a clip is made

- One fresh inspector process per clip x theme x mode, mounted at the entry's size and `scale` (2 by default).
- The setup lines, then the before value through `setting id=<dial> value=<v>`: the dial's own writer, as a Settings view
  calls it. A rest, then `render` of the before frame.
- The change: the after value written with `settle=false` (a slider is swept through its values; an `after` list steps
  through several values, each held), or the entry's action (a click, a `goto`, an inspector state). Then `frames <step>`
  + `render` per captured frame on the inspector's 16 ms clock until the window holds still (renders of a still scene
  are identical) or `capture` seconds pass (`watch: none` never judges stillness: a countdown's digit and thin bar move
  too little to tell).
- The crop: the union of the region keys (`find`, in both states) grown to 16:9, at least `minWidth` DIP wide, kept
  inside `within` and the window. Never the whole window unless the window is the subject (the full screen, the mini
  player).
- The frames are resampled to 15 fps (motion can be slowed with `slow` so a 300 ms flight is readable), cropped, scaled to
  the shot, merged where identical (sub-rectangle frames do the rest), composed into the frame (below) and encoded with
  libwebp's WebPAnimEncoder through Pillow: lossy, quality 70 (an entry may start higher: dim lyrics on a dim ground smear
  into a blur of their own at 70, so the lyrics clips start at 90), mixed frames, one key frame, no alpha. Not ffmpeg: its
  `libwebp_anim` files decoded several times slower in the engine (`ops/release/wavee/README.md`, Motion highlights).
- A still stretch inside the change is kept as it was, unless the entry sets `maxStill` (a marquee waits 2.5 s before
  its pass: that wait is cut to 0.4 s).
- Sound dials (Crossfade, Normalization, Loudness level) are diagrams drawn by the script: two waveforms sliding into a
  crossfade, loud and quiet songs evening out, the level stepping Quiet, Normal, Loud. Their colours are read from the app
  (the Settings page rendered and dumped in that theme and mode: background, ink, quiet ink, accent). No audio, ever.

## The frame

Every clip, captured or drawn, is composed into the same restrained frame (owner, 2026-11-19: framed, "not too bland",
not too much), set in `clips.json` `defaults.frame` per theme and app mode and drawn by `Framer` in `record.py`:

- The canvas is 640x360. Behind the shot, the theme's quiet ground: WinUI a Mica-like diagonal from the ground (#202020 /
  #F3F3F3) to a low-saturation tint of the accent; Classic the ground (#202020 / #F2F2F2) with a soft wash of the
  accent's tint from the top edge; Zune the flat ground (#161616 / #F5F5F5) with one faint radial glow of the accent
  (#FF3CA6 / #D51878) in the top right corner. Each moves at most 10-16 % toward its tint: barely visible.
- The shot sits at 534x300 (16:9) with 53 px at the sides and 26 px above: the theme's corner radius (WinUI 8, Classic 8,
  Zune 0), a 1 px hairline (white 8 % on dark, black 8 % on light) and a soft shadow under it (14 px blur, 6 px down,
  black at 55 % on dark, 16 % on light). No reflection, bezel or glow round the shot, and no zoom or pan.
- Under the shot, the before/after cue: two 6 px dots 8 px apart in the ink at low alpha (16 % off, 60 % on). The first is
  lit during the before hold, the second during the after hold, cross-fading over the first 0.5 s of the change; a
  clip in takes (Fast navigation) lights the first dot for the first take and the second for the last.
- The frame is static, so it is paid for once, in the key frame; over the budget, the encoder lowers the quality, which
  only the changing shot pays for.

## Add a clip

1. The dial must exist in the registry (`setting` lists every dial; `Screens/SettingsSpec.*.cs`).
2. Add an entry to `clips.json` (its `_doc` names every field): `clip`, `dial`, `kind` (`state`, `motion` or `diagram`),
   `themes`, `status`, the surface (`route`, `mountArgs`, `setup`), `before` and `after` (or `value` and `action`), and
   the `region` (node keys from `find`, or a rect). Any field can differ per theme: `{"winui": ..., "default": ...}`.
3. Find the keys with the inspector: `python -I tools/inspect.py --exe <exe> -c "mount ..." -c "find prefix=<p> visibleOnly=true"`.
4. Record it in one theme and mode, read the contact sheet, then all of them; `--check`.
5. Commit the clips, and register the dial in `s_clips` (`Screens/SettingsExplain.cs`) for exactly the themes whose clips
   now exist in both modes, with the caption `settings.explain.<dialId>` in `assets/loc/en-US.json`. The Release check
   then holds the badge to its clips.

## Budget

At most **120 KB per clip** and **9 MB for the whole folder** (it ships inside every package). The encoder retries at
lower quality (from the entry's quality down to 55 at 15 fps, then 12 fps from 55 down to 25) until a clip fits.

`--check` fails on a clip over 120 KB, a total over 9 MB, a file that is not a 640x360 animated WebP, a loop outside 2-4 s
or a frame faster than 15 fps, and warns on a file the manifest does not name. With `--strict` a clip the manifest expects
for a non-pending entry and lacks is an error too. The VP8X alpha flag is reported, not judged: WebPAnimEncoder sets it
whenever a sub-rectangle frame leaves pixels transparent, as on the shipped What's new motions; the source frames are RGB.

The manifest expands to 122 clips (`--list`); 92 are committed, 30 pending. The committed set is 4.02 MB (45 KB a clip
on average; per theme and mode: Classic 16 clips, about 755 KB; WinUI 17, about 745 KB; Zune 13, about 555 KB). Most run
5-70 KB; the heavy ones are a page swap filmed twice (Fast navigation, 60-104 KB) and the full-screen layouts stepping
through four arrangements (83-97 KB).
