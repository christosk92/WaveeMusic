# Video seek preview: the picture under the pointer on the seek rail (spike)

Status: **spike**. The code on this branch compiles clean, renders in all three themes and is traced in the headless
inspector, against generated sheets and against the REAL sheets of a captured manifest (fetched from the CDN through the app's
own sheet store). It has **not** been run inside a real playing session. Section 10 lists what is open.

What it is: while the pointer hovers or scrubs the seek rail of a video, the rail's tip shows the frame at that time, the
time, and on an episode the chapter. YouTube calls it the seek preview.

![The seek preview in WinUI, Zune and Classic over a fullscreen video](video-seek-preview/themes-fullscreen-dark.jpg)

![The seek preview on the player bar's rail, light, 150 %](video-seek-preview/themes-player-bar-light.jpg)

The frames in these renders are generated (`tools/seekpanels.py`): each carries its own time in a corner, so a render shows
whether the picture and the tip's time agree.

## 1. Where the pictures come from: `seekpanels`

The v9 video manifest Wavee already fetches (`/manifests/v9/json/sources/{id}/options/supports_drm`) carries a block the
parser ignored. Measured 2026-11-16 on three PlayReady music videos and four video podcast episodes (market NL, Premium):

```json
"seekpanels": {
  "base_urls": ["https://seekpanels.spotifycdn.com/"],
  "templates": ["seekpanels/v1/origins/<o>/sources/<id>/<e>/{{variant_id}}/{{panel_id}}.jpg?__token__=<signed>&fauth=<signed>"],
  "variants": [
    { "id": "0", "mime_type": "image/jpeg", "frame_width": 180, "frame_height": 100, "rows": 6, "columns": 6,
      "frames": { "frame_deltas_in_seconds": { "values": [0, 1, 1, 1, "..."] } } },
    { "id": "1", "mime_type": "image/jpeg", "frame_width": 452, "frame_height": 254, "rows": 3, "columns": 3,
      "frames": { "frame_deltas_in_seconds": { "values": [0, 1, 1, 1, "..."] } } }
  ]
}
```

| Fact | Measured |
|---|---|
| Who has it | all 3 PlayReady music videos, all 4 podcast episodes (2 shows) |
| Frame step | music videos 1 s; podcasts 8 s or 10 s (it is a per-frame list, so it may vary) |
| Frame count | duration / step + 1 (a 245.1 s video: 246 frames) |
| Variant "0" | 6 x 6 cells; 180 x 100 for 16:9, the width or height follows the aspect (146 x 100, 178 x 74) |
| Variant "1" | 3 x 3 cells; 452 x 254 for 16:9 |
| Panel numbering | from 0; panel `ceil(frames / cells)` answers 404 |
| Cell order | reading order (left to right, top to bottom) |
| Last panel | cropped to the rows it uses (30 frames of 36 -> 1080 x 500, not 1080 x 600) |
| Sheet weight | variant "0": 50-100 KB each, 370-470 KB for a whole music video; variant "1": 1.5-2.0 MB for a whole music video |
| What the CDN needs | the signed query only: 200 with no bearer and no `Origin`/`Referer`; 403 without the query |
| Lifetime | token about 168 h; `Cache-Control: public, max-age=10800` |

`spritemaps` in the same manifest is **not** the seek sheet: it is one frame (`"number": 1`, half the natural size; for the
measured music video, its title card).

**The time rule**, and how it was proven. Frame `i` shows the video at the running sum of the deltas. A video podcast's stream
is unencrypted, so real frames were decoded with ffmpeg from the segment that starts at that time and compared with the sheet:

```
frame  T(i) s  panel  cell | diff to predicted cell | best match of all 177 cells | diff of the frame half a step later
    1       8      0     1 |                   3.14 |                     1       |                              56.57
   36     288      1     0 |                   4.67 |                    36       |                              10.90
  143    1144      3    35 |                   3.77 |                   143       |                              12.04
  175    1400      4    31 |                   6.04 |                   175       |                              51.12
```

Seven frames across all five sheets: the predicted cell was the best match every time, and the frame half a step later always
matched worse, so the rule has no offset. PlayReady frames cannot be decoded for comparison; for those the same rule holds
by construction (same block, same generator) and by order (title cards first, credits last).

## 2. The shape

```
manifest JSON
   |  Playback.Video.Manifest.Parse            <-- new: SeekPanels.Parse(root, manifestId)
   v
Playback.Video.VideoSource.SeekPanels          <-- new init member; null = no sheets
   |  Playback.Video.Source (the resolved source signal, unchanged)
   v
Shell.BarSeekRail (core host of every seek rail: player bar, stage transport, on-media strip)
   |  PlayerBarFacts.Seek.PreviewRail(hasSheets, mode, SeekRail.PreviewFits(onMedia, railPx))
   |  SeekRailProps.HoverTip = chapterRail || previewRail      <-- was ChapterRail
   v
Shell.BarRailTip (core host; was BarChapterTimeline)           one tip per rail
   |  sheets -> Player.SeekPreview bubble, PopupChrome.Raw (the tip fade)
   |  no sheets, chapters -> Player.ChapterTip bubble, as before
   v
theme template SeekPreview(SeekPreviewProps)                   the look: plate, corners, ring, type, motion, the lift
   |  props.Frame(width, corners)
   v
Shell.SeekFrame (core host) + SeekFrameBoxes                   the picture: which sheet, which cell
   |  Playback.Video.SeekSheets.Source(url)
   v
Playback.Video.SeekSheets                                      the sheet as a FILE: fetched once, keyed without its signature
```

### The model (`Playback/Playback.Video.SeekPanels.cs`, pure)

```csharp
public sealed class SeekPanels
{
    public sealed record Variant(string Id, int FrameW, int FrameH, int Rows, int Columns, int[] TimesSec)
    {
        public int FrameAt(long ms);       // the LAST frame at or before ms (binary search)
        public int RowsIn(int panel);      // the last panel is cropped to the rows it uses
    }
    public Variant Coarse { get; }         // the most frames per panel: what a sweep reads
    public Variant Fine { get; }           // the largest frame: what a resting pointer sharpens to
    public string Url(Variant v, int panel);
    public static SeekPanels? Parse(JsonElement root, string key);
}
```

### The contract (`Theming/Contracts`)

```csharp
public static readonly ViewKey<SeekPreviewProps> SeekPreview = new("Player.SeekPreview");

public enum SeekRailSurface : byte { Bar, Stage, OnMedia }

public sealed record SeekPreviewProps
{
    public required Func<float, CornerRadius4, Element> Frame { get; init; }   // the picture's core host at the theme's width and corners
    public required float Aspect { get; init; }
    public required Func<string> Time { get; init; }                           // bound
    public required Func<string> Label { get; init; }                          // the chapter, the scrub's speed, or the precise-seeking hint
    public required Func<bool> HasLabel { get; init; }
    public required IReadSignal<bool> Scrubbing { get; init; }
    public SeekRailSurface Surface { get; init; }                              // what the tip must stay clear of is the surface's
}
```

`SeekRailProps.ChapterRail` is renamed `HoverTip` ("the rail has a tip that follows the pointer"): the three rail templates
wire their hover handlers on it. Nothing else in a rail template changes.

## 3. The picture: two layers, no wrong frame

```
SeekFrame (core)                         a clip window, width x width / aspect, the theme's corners
 +- coarse sheets (variant "0")          follow the pointer at once
 |    each sheet: an ImageEl at the FILE's pixel size (so the decode is the file's, whatever the tip's width)
 |    under one bound transform:  scale a cell to the window, translate cell (col, row) into it
 |    and one bound opacity:      1 while the frame under the pointer is one of this sheet's cells
 +- fine sheets (variant "1")            follow only once the pointer rested 120 ms (SeekRail.PreviewSharpenMs)
      hidden until the settled frame IS the frame under the pointer
```

- **A move inside a sheet repaints and nothing else.** The cell is a compositor transform; no component re-renders, no
  layout runs for the picture, no image is fetched.
- **A sweep fetches no fine sheet.** The fine layer's frame follows a 120 ms dwell; while it is behind the pointer its layer
  is hidden and the coarse layer shows through.
- **No wrong picture.** A sheet's transform and opacity read the same signal, so they change in the same bind flush; a
  sheet that has not decoded draws nothing, and the theme's plate (`Tok.MediaLetterbox`) shows.
- **The last sheet.** Its file is shorter than the grid. The image box stays the grid's size with `ImageFit.Fill`, and the
  transform's y scale is multiplied by `rowsUsed / rows`, which undoes the stretch.
- **Which sheets are mounted.** Up to 12 coarse sheets (`SeekRail.PreviewMountAll`) all stay mounted, so a jump across a
  music video never waits. A longer video keeps the pointer's sheet and one each side. Fine sheets: the settled one and one
  each side.
- **Memory while the tip is open** (computed, not measured): a coarse sheet is 1080 x 600 x 4 = 2.6 MB decoded, so a music
  video's seven are 18 MB; three fine sheets are 12 MB. The tip unmounts them when it closes.

### Sheets on disk (`Playback/Playback.Video.SeekSheets.cs`)

The engine's image pipeline fetches an `https` source exactly as written, so a signed sheet url works in it. But it keys an
image by the whole source string, in memory (`ImageCache.SourceKey`) and on disk (`DiskImageCache.Resolve`), and a sheet's
url ends in a signature that changes with every manifest fetch (ten minutes). Left there, the same sheet would miss, download
and be stored again each time, and a signature that expired fails for good under that key.

```
sheet url (signed)
   |  SeekSheets.KeyOf        the url without its query
   v
<image cache>/seekpanels/<sha256(key)>.jpg     fetched once (3 at a time, on the pool), written atomically
   |  the cell for that key: "" until the file exists, then its path      (set on the UI thread)
   v
ImageEl.Source                                 a local FILE: no network, no second copy in the image disk cache
```

- **Prefetch.** When the tip opens, `SeekFrame` asks for every coarse sheet, the pointer's first and then outwards (up to
  48). By the time the pointer travels, the sheets are files. A music video is 7 sheets; a three-hour podcast about 40.
- **A failure** (a refused signature, no network) is retried no sooner than 30 s later, with whatever url is current then.
- **The folder** sits inside the image cache's, so the Storage tab counts it with the images. It is trimmed once per process:
  files older than 14 days, then the oldest beyond 96 MB.
- **Logs** name a sheet as `variant/panel.jpg` only. The url carries a signature and never reaches a log.
- A local file template (the inspector's fixture) passes through untouched.

## 4. Where the picture shows

```
rail                               picture in the tip?
player bar                         yes
stage transport (fullscreen)       yes
on-media strip, rail >= 420 DIP    yes
on-media strip, rail <  420 DIP    no: the time readout, as today      (the mini player, a docked card at its resting width)
DVR window / live line             no
no sheets (local file, module)     no: the time readout, as today
```

The 420 (`SeekRail.PreviewMinRailPx`) is a first proposal: under it the picture would cover most of the video it previews.
The on-media strip is told apart by its chrome feed (only it has one); `SeekRailSurface` carries the surface to the theme.

One tip per rail: while a rail has the preview, the plain time readout stays shut, and on an episode with sheets the
chapter's title rides in the preview instead of a second bubble.

## 5. Precise seeking

While a scrub over a seek preview lasts, the pointer's distance from the rail (up or down) slows the scrub:

```
pointer off the rail's line     the playhead follows at       the tip's second line
within 48 DIP                   the pointer's speed           track: "Pull up for precise seeking"; episode: the chapter
48 to 128 DIP                   half                          "Half speed"
beyond 128 DIP                  a quarter                     "Quarter speed"
```

Until the pointer first leaves the near band the rail follows it absolutely, exactly as a rail without a preview does. From
then on the gesture is relative (the travel since the last move, times the rate), so coming back to the rail does not jump
the playhead to the pointer. At a quarter, a 1400 DIP rail over a four-minute video moves one second (one preview frame) per
23 DIP. Only rails with a picture have it: an audio rail scrubs as it always did.

## 6. The three looks

No board draws this surface: these are proposals in each theme's existing tip language. Every number is in the theme's
`Rules` record.

**WinUI: a thumbnail flyout.** The acrylic card of every WinUI flyout (overlay radius 8, flyout ring and elevation). The
picture is inset 4 with radius 4, so the corners are concentric. One caption line: the time (14, semibold, tabular), the
label beside it (12, secondary). It stops on the page gutter (16) when the window clamps it. On the player bar, where the
transport buttons sit over the rail, the tip rises above the whole bar (`PreviewLiftBar`), so play and skip stay in view.

```
 .----------------------------.
 | .------------------------. |
 | |                        | |   232 x 130
 | |        picture         | |
 | '------------------------' |
 |            2:06            |
 '----------------------------'
              |  14 above the rail's line (54 on the player bar: above the bar)
 ======o=====================
```

**Zune: the readout's plate, grown around a picture.** Square, `R.Layer.FloatingChrome`. The picture runs to the plate's
edges, the rail's own 2 px accent line closes it, and the time stands under it in the plate's large type (24/30), left like
every Zune caption. The label is the line under the time (13/18, two lines at most).

```
 +----------------------------+
 |                            |   248 x 139
 |          picture           |
 |                            |
 +============================+   2 px accent
 | 2:06                       |
 +----------------------------+
```

**Classic: no card.** The picture floats alone (card radius, flyout ring and elevation) and the time rides under it in the
readout's bubble drawn as a pill; the label rides in the pill beside the time.

```
 .--------------------------.
 |                          |     224 x 126
 |         picture          |
 '--------------------------'
          ( 2:05 )
```

Motion, all three: the host's tip fade (`PopupChrome.Raw`, the Slider's value tip) plus an `Enter` from a few DIP below
(WinUI and Classic also 3-4 % small), so the tip rises out of the rail. The fine sheet arrives as a 160 ms fade.

## 7. An unencrypted video manifest plays (video podcasts)

A video podcast's manifest names no key system. `Manifest.Parse` returned null unless it found a PlayReady entry, so such an
episode resolved to "no video", played as audio, and had no sheets either.

```
encryption_infos                     before                 now
has "playready"                      PlayReady source       PlayReady source (unchanged)
empty or absent                      null: no video         VideoSource.Segmented: the same descriptor, no PSSH, no key, no relay
has key systems, none PlayReady      null: no video         null: no video (nothing here has a CDM for it)
```

`VideoSource.IsSegmented` (a descriptor-backed source, protected or not) now routes the open and the manifest invalidation;
`IsDrm` keeps meaning "has a licence". The native session already attaches a clear init without a licence handle
(`PrSession.cpp`: "lic == 0 is legal: clear content"), and its demuxer marks a track protected only from `tenc`.

What was checked, and what was not:

- The app's own parser over captured manifests: four podcast episodes parse as clear with six rungs (180 to 1080), audio and
  a descriptor with no PSSH and no key id; four PlayReady videos are unchanged.
- The engine's native demuxer probe (`FgPrProbeFile`) over real segments: two podcast episodes read as `encrypted=0`,
  568 x 320, NAL length 4, with keyframes; a PlayReady control reads as `encrypted=1`.
- **Not run: an actual playback.** Whether the protected media engine renders a clear source needs the running app. Look for
  `attach.deferred.nokey` and `attach ... license=none` in the log. Two known costs: a clear session waits for the init plus
  one segment before it attaches, and the engine labels a failure of such a session as a licence failure.

## 8. What the trace says

Inspector, 1440 x 960, generated sheets unless said otherwise:

- A drag of 30 DIP per frame for 20 frames (WinUI, fullscreen): the picture moved exactly 30 DIP every frame and stayed on
  the release point. 0 snaps, 0 instants, 0 component re-renders during the drag, 0 images applied. CPU per frame: 3.1 ms
  average, 5.4 ms worst.
- Precise seeking: a drag of 20 DIP per frame along the rail while pulling 8 DIP per frame away from it. The tip moved 20
  per frame for 6 frames, then 10 for 10 frames, then 5. 0 snaps.
- Opening the tip from nothing (`hover ... trace=40`, gated): 0 motion failures. With `motion=reduced` (Zune): 0 motion
  failures, the picture at its place two frames after the hover.
- `lint min=warn` with the tip open, `--gate error`: 0 errors and 0 warnings in WinUI, Zune and Classic. (Zune lists one
  todo warning on the remaining-time label, which this branch does not touch.)
- **Real sheets.** `seekManifest=` with the captured manifest of a PlayReady music video: the store fetched 13 sheets
  (928 KB) from the CDN into the scratch profile, and the tip showed the video's own frames, also right after a jump
  across the bar.
- The merged tip (`seekPreview=episode`): the picture, the time and the chapter ("0:23 Opening") in all three themes.

Two bugs the renders found, both fixed here: after a scrub was released the tip fell back to the last HOVERED point (the
chapter tip had the same jump), and on WinUI's player bar the tip covered the transport buttons.

## 9. Staging it in the inspector

```text
python -I tools/seekpanels.py --out C:/WAVEE/scratch/<lane>/run/assets/seekpanels     # once per run copy
python -I tools/inspect.py --exe C:/WAVEE/scratch/<lane>/run/Wavee.exe \
  -c "mount w=1440 h=960 theme=zune route=home video=fullscreen player=fake chrome=shown seekPreview=on images=real" \
  -c "hover x=900 y=866" -c "frames 60" -c "render path=C:/WAVEE/scratch/<lane>/seek.zune.png"
```

- `seekPreview=on` (beside `video=`) publishes a source that carries sheets in the two measured sizes, 246 frames at 1 s.
  The sheets are files beside the exe, `assets/seekpanels/<variant>/<panel>.jpg`; none are bundled.
- `seekPreview=episode` plays an episode with chapters instead of the video track: the merged tip.
- `seekManifest=<file>` takes a captured manifest and fetches its real sheets from the network through the sheet store (the
  wire stays refused for everything else). A fetch lands on the wall clock, so wait between commands.
- Rail lines at 1440 x 960, fullscreen: WinUI `y=884`, Zune `y=866`, Classic `y=887`. Player bar (with `video=docked`):
  WinUI `y=942`, Zune `y=889`, Classic `y=924`. `images=real` is needed to see the frames.
- Precise seeking: `trace 30 drag=-20 dragDy=-8 dragX=1200 dragY=884 dragFrames=24 keys=seek-frame`.

## 10. Open, in the order it matters

1. **A real playing session.** The tip has not been seen over a live video in the running app. Everything upstream of that
   is checked (the parse on real manifests, the real fetch, the lookup against decoded frames).
2. **Clear podcast playback is not run** (section 7).
3. **A signature that expires while the source stays live.** A failed sheet is retried with the same source's url; only a
   re-resolved source brings a new signature. The token lasts about a week and a manifest ten minutes, so it should not
   arise, but nothing forces a re-resolve for it.
4. **No sheets for a local file or a module's video.** The tip stays the time. Generating frames needs a second decoder.
5. **The windowed coarse path** (a video with more than 12 coarse sheets) runs the same code the fine layer runs in every
   render here, but no staged source has that many coarse sheets.
6. **Not rendered:** the pop-out window (the inspector has no second window; it hosts overlays like the main one) and touch
   (a touch has no hover, so the tip shows from press to release, as in the drag trace). By design: the keyboard ladder
   opens no tip, and the mini player draws its own rail and has none.
7. **The looks and numbers** (sizes, the 420 rule, the 120 ms dwell, the 48 and 128 DIP bands, the 12-sheet cap) are
   proposals with no board behind them.

## 11. Files

| File | What |
|---|---|
| `Playback/Playback.Video.SeekPanels.cs` | the model: parse, `FrameAt`, `Url` (new) |
| `Playback/Playback.Video.SeekSheets.cs` | the sheets on disk: key, fetch, prefetch, trim (new) |
| `Playback/Playback.Video.cs`, `.Source.cs` | `VideoSource.SeekPanels`, `IsSegmented`, `Segmented`; `Manifest.Parse` reads the block and lets a clear manifest through |
| `Shell/Shell.PlayerBar.Chapters.UI.cs` | `BarRailTip` (was `BarChapterTimeline`), `SeekFrame`, `SeekFrameBoxes` |
| `Shell/Shell.PlayerBar.UI.cs`, `Shell.PlayerBar.cs` | the rail's preview decision, its surface, precise seeking; the hover point follows a drag; the rules and constants |
| `Theming/Contracts/ViewKeys.Player.cs`, `PlayerTransportProps.cs` | `Player.SeekPreview`, `SeekPreviewProps`, `SeekRailSurface`, `HoverTip` |
| `Themes/{WinUi,Zune,Classic}/Structure/...` | the template, its `Rules` numbers, the registration, the renamed hover fact |
| `assets/loc/en-US.json` | the precise-seeking strings |
| `Screens/Diagnostics.Inspect*.cs`, `tools/seekpanels.py` | `seekPreview=on|episode`, `seekManifest=`, the generated sheets |
