# Frame bench — every frame, measured

`--frame-bench` drives Wavee through fixed, wall-clock scenarios with **real paced frames** and records CPU, memory and GPU
for every one of them through the engine's frame ledger (`FrameLedger`, `..\fluent-gpu\src\FluentGpu.Engine\Hosting\FrameLedger.cs`).
It writes per-scenario CSVs and a `frame-bench-summary.json` that `ops/tools/frame-bench-compare.ps1` diffs before/after.
It is the measuring stick for optimisation work: run it on the baseline, run it on the change, compare.

It replaces `--perf-bench` for frame cost (that arm stays, but its frames suppress vsync and its frame times are stale).

## Running it

Always a Release build, always beside (never instead of) a running Wavee: `--profile` gives the bench its own single
instance, settings and caches.

```powershell
dotnet build Wavee.slnx -c Release
$exe = 'src\apps\Wavee\bin\Release\net10.0\Wavee.exe'

# Offline (no account, no network): the --fake catalogue, a silent audio endpoint.
& $exe --fake --profile C:\scratch\bench-fake --frame-bench --probe-out C:\scratch\bench\fake-base

# A subset, longer windows:
& $exe --fake --profile C:\scratch\bench-fake --frame-bench=lyrics,idle-playing --bench-sec 15 --probe-out C:\scratch\bench\lyrics
```

Real data (a signed-in bench profile: CDN images, real metadata, FLAC/Vorbis decode, real synced lyrics) needs `--bench-real`
spelled out — without `--fake` the arm refuses to run otherwise. **It plays on that account**: Spotify allows one active
device, so it takes playback over from wherever the account is playing, and stops it at the end.

```powershell
& $exe --profile C:\WAVEE\perf\bench-profile --frame-bench --bench-real --bench-label cold --probe-out C:\scratch\bench\real-cold
& $exe --profile C:\WAVEE\perf\bench-profile --frame-bench --bench-real --bench-label warm --probe-out C:\scratch\bench\real-warm
```

The first run after copying a profile without its image/audio caches and library database is the **cold** case; the
second is **warm**. Label them (`--bench-label`) and compare the two summaries.

| Flag | Meaning |
|---|---|
| `--frame-bench[=a,b]` / `--frame-bench a,b` | Run every scenario, or the named ones (run order is fixed; unknown names are reported). |
| `--bench-sec N` | The measured window per scenario, seconds (3–120, default 10). |
| `--bench-warmup-sec N` | The warm-up before each window, seconds (0–30, default 2). |
| `--bench-real` | Real data. Required without `--fake`. Sets the profile to lossless audio and AI lyrics on (a `--profile` run's settings live in the profile, not the owner's). |
| `--bench-uris k=uri,...` | Explicit targets: `track`, `lyrics` (word-synced), `lyrics-line`, `album`, `playlist`, `artist`, `big` (`liked` = Liked Songs). |
| `--bench-label NAME` | Stored in the summary (`cold`, `warm`, a branch name). |
| `--probe-out DIR` | Where everything is written (default `<profile>\bench`). |
| `--fake-video PATH` | With `--fake`: enables the `video` scenario. |

Real targets come from the profile itself: `WaveeMusic\play-recency.json` (what was played, newest first) and
`WaveeMusic\history.json` (where the user navigated), unless `--bench-uris` names them. The lyrics scenarios ask the
lyrics store for the candidates' documents *without playing them* and take the first word-synced (`Syllable`) and the
first line-synced one.

The window is made topmost for the run: a covered window stands its presents down, which would measure the stand-down.
The full suite at the default windows takes about 3 minutes.

## Scenarios

| Name | What runs |
|---|---|
| `idle` | Nothing playing, Home. The loop should block: presents/s near 0. |
| `idle-playing` | A track playing on Home, sidebar visible, rail closed. |
| `home-scroll` | Home with programmatic glide sweeps (85 % of a viewport every 0.55 s, 8 viewports deep and back). |
| `nav-burst` | A navigation every 0.7 s across albums, artists and playlists, Home every fourth hop. |
| `playlist-open` | The same playlist opened (1.2 s) and left for Home (0.6 s), repeatedly. |
| `playlist-scroll` | The big list (Liked Songs by default) with glide sweeps 30 viewports deep. |
| `lyrics` | A word-synced track playing, the lyrics rail open. |
| `lyrics-line` | A line-synced track playing, the lyrics rail open (skipped when none is found). |
| `stage-lyrics` | The fullscreen stage, lyrics mode. |
| `stage-visualizer` | The fullscreen stage, visualizer mode (live FFT). |
| `track-change` | An album playing; alternately a skip and a seek to 3.5 s before the end (the gapless roll), every 4 s. |
| `video` | `--fake --fake-video` only: a track with its video, the rail in video mode. |
| `ledger-overhead` | The steady fullscreen visualizer in four windows, ledger off/on/off/on (see below). |

Each scenario sets itself up, runs the warm-up, then the measured window. The loop is FluentApp's own (`RunFrame`,
`TickDetachedHosts`, `WaitRequestWithDetached` → `WaitForWork` → `NoteLoopWait`); only the wait's timeout is capped so the
next scripted action and the window's end land on time. Pass-granular GPU timing is on for the run.

## What it writes

Per scenario: `<name>-ui.csv`, `-render.csv`, `-gpu.csv`, `-memory.csv`, `-audio.csv` (one row per record, times in ms since
the window start) and `<name>.fgl` (the binary ledger; `LedgerSnapshot.ReadFile`). Then `frame-bench-summary.json`,
`frame-bench-summary.txt` and the same table on stdout:

```
scenario          pres/s  uiP50  uiP99  rdP50  rdP99    uiC    rdC   othC  proc%  gpP50  gpP99 gpBsy%  miss alloc/f   wsMB vramMB   gc0
```

## The streams (the ledger)

| Stream | One record per | Key fields |
|---|---|---|
| UI | `AppHost.RunFrame`, early-outs included | entry / pump / Paint's six phases / return stamps; the loop wait before it (+ kind); `exit` (Painted, Idle, Gated, Parked, VideoOnly, ...); UI-thread CPU; allocated bytes; GC counts and pause; the process's cumulative cycles; `publishSeq`; nodes, slices, components, damage coverage |
| render | render-thread turn | wait start / turn start / slot open / present done / end; `kind` (Fresh, Motion, TickSpent, CatchUp, Bare); `outcome` (Recorded, CompositeOnly, Elided, SkipSubmit); the present split (stage/record/submit/fence/latency/present/video ms); render-thread CPU; allocations; `publishSeq` (joins UI); `submitSeq` (joins GPU); tiles rastered; damage coverage; missed ticks |
| GPU | retired whole-frame timestamp pair | GPU ms; start/end on the QPC timeline (`GetClockCalibration`); `submitSeq`; per-pass ms (uploads, baked blur, clear, scene, glyph band, tile raster, offscreen, composite) |
| memory | 250 ms (own thread) | working set, private bytes, managed, GC heap/committed, VRAM local/non-local, image cache, glyph atlas, process CPU (GetProcessTimes) |
| audio | 250 ms | device dry edges (the WASAPI buffer drained between two writes — device/OS side), app xruns (the feed ring ran empty — producer side), the window's minimum device padding |

`--fg ledger` (or `--fg ledger=PATH`) turns the same ledger on in an ordinary run; with a path, the binary dump and the
five CSVs are written there when the window closes.

## The summary's fields

Per scenario, `metrics` is a flat map. `X.avg/.p50/.p95/.p99/.max` are distributions (nearest rank).

| Field | Meaning |
|---|---|
| `presentsPerSec`, `presents` | Render turns that called Present on the main swapchain. 120 Hz caps it at ~120. |
| `framesPerSec`, `frames`, `paintedFrames` | `RunFrame` calls (incl. early-outs) and the ones that ran Paint. |
| `presentIntervalMs.*` | Present-to-present time: the smoothness number (8.33 ms at 120 Hz). |
| `uiCpuMs.*`, `uiFrameMs.*`, `paintedFrameMs.*` | UI-thread CPU per RunFrame; its wall time; the wall time of painted frames. |
| `renderCpuMs.*`, `renderTurnMs.*` | Render-thread CPU / wall per presented turn. |
| `otherCpuMs.*` | Per UI frame interval: process CPU − UI − render (decode, audio, network, GC threads). |
| `uiCores`, `renderCores`, `otherCores`, `processCores` | Average cores used over the window; the first three add up to the fourth. |
| `processCpuPct` | Task-Manager style: process CPU / (wall × processors). `processCpuPctTimes` is the same from `GetProcessTimes` (cross-check). |
| `gpuMs.*`, `gpuBusyPct`, `gpuPass.*Ms` | GPU execution per frame; Σ GPU ms / wall; average per-pass time over frames with a pass timeline. |
| `uiAllocBytesPerFrame`, `renderAllocBytesPerTurn`, `processAllocBytesPerSec` | Managed allocations. |
| `gc0`, `gc1`, `gc2`, `gcPauseMs` | Collections and total pause in the window. |
| `missedVsyncs`, `missedTicks` | The host's missed-vsync counter; compositor ticks a live-motion present skipped. |
| `workingSetMB`, `privateMB`, `managedMB`, `gcHeapMB`, `vramLocalMB`, `vramNonLocalMB`, `imageCacheMB`, `glyphAtlasMB` (`.avg`, `.peak`) | Memory. On a UMA iGPU (Adreno) VRAM LOCAL is system memory and is inside the working set. |
| `audioDeviceDryEdges`, `audioXruns`, `audioPaddingMinMs` | Audio health (none under `--fake`: its endpoint is silent). |

`ledger-overhead` reports `framesPerSec.off/on`, `uiCpuUsPerFrame.off/on`, `runFrameWallUs.off/on`, `processCpuPct.off/on`
and their differences (`overhead*`), measured around the loop itself — the ON figure includes everything the ledger does.

### How CPU is measured

Per-thread CPU is `QueryThreadCycleTime`, the process total `QueryProcessCycleTime`; both are cycle counts, converted to
milliseconds at the ledger's calibrated rate (the highest cycles-per-ms seen over spans the thread was known to be running:
a spin at enable, then every frame and turn). Where the counter tracks the core clock (Snapdragon X), a converted figure
reads as CPU time at the peak clock, so compare runs on the same machine and power plan. `processCpuPctTimes`
(scheduler-tick accounting) is the independent cross-check.

## Comparing

```powershell
pwsh ops/tools/frame-bench-compare.ps1 -Before C:\scratch\bench\base -After C:\scratch\bench\lane-a
pwsh ops/tools/frame-bench-compare.ps1 C:\scratch\bench\real-cold C:\scratch\bench\real-warm -All -Threshold 5
```

Each row: before, after, delta, % change and `REGRESSED` / `improved` when the change passes `-Threshold` (default 10 %)
and the metric's absolute floor (0.05 ms/cores, 1 MB, 1 %, 64 B, 1 count). Direction: presents/frames per second and the
audio padding minimum are higher-is-better, run-describing counts are neutral, everything else is lower-is-better.
`-FailOnRegression` exits 1 on a regression; `-Scenario` narrows; `-All` prints every metric.

Run-to-run noise on a busy machine is easily 5–10 % for p99 figures: compare medians first, repeat a run that flags
something before believing it, and never compare a fake run with a real one (the tool warns).

## Source

- Engine: `FluentGpu.Engine/Hosting/FrameLedger*.cs`, `AppHost.Ledger.cs`, `Media/Playback/Audio/AudioHealth.cs`;
  Windows seams `FluentGpu.Windows/Pal/Win32LedgerSampler.cs`. Tests: `FluentGpu.Engine.Tests/FrameLedgerTests.cs`.
- App: `src/apps/Wavee/Screens/Diagnostics.Probe.FrameBench.cs` (the arm), `Diagnostics.FrameBench.cs` (pure: options,
  targets, summary maths, JSON, table). Tests: `src/apps/Wavee.Tests/FrameBenchTests.cs`.
- Compare: `ops/tools/frame-bench-compare.ps1`.
