# Frame bench — every frame, measured

`--frame-bench` drives Wavee through fixed, wall-clock scenarios with **real paced frames** and records CPU, memory, GPU and
audio health for every one of them through the engine's frame ledger (`FrameLedger`,
`..\fluent-gpu\src\FluentGpu.Engine\Hosting\FrameLedger.cs`). It writes per-scenario CSVs and a `frame-bench-summary.json` that
`ops/tools/frame-bench-compare.ps1` diffs before/after. It is the measuring stick for optimisation work: run it on the
baseline, run it on the change, compare.

It replaces `--perf-bench` for frame cost (that arm stays, but its frames suppress vsync and its frame times are stale).

## Running it

Always a Release build, always beside (never instead of) a running Wavee: `--profile` gives the bench its own single
instance, settings and caches. Run one bench at a time.

```powershell
dotnet build Wavee.slnx -c Release
$exe = 'src\apps\Wavee\bin\Release\net10.0\Wavee.exe'

# Offline (no account, no network): the --fake catalogue, a silent audio endpoint.
& $exe --fake --profile C:\scratch\bench-fake --frame-bench --probe-out C:\scratch\bench\fake-base

# A subset, longer windows:
& $exe --fake --profile C:\scratch\bench-fake --frame-bench=lyrics,idle-playing --bench-sec 15 --probe-out C:\scratch\bench\lyrics
```

Real data (a signed-in bench profile: CDN images, real metadata, FLAC/Vorbis decode, real synced lyrics) needs `--bench-real`
spelled out — without `--fake` the arm refuses otherwise — **and** `--profile`: the run plays on that profile's account and
sets that profile's settings (lossless audio, AI lyrics on; both restored at the end), so it refuses to touch the default
profile. Spotify allows one active device: the run takes playback over from wherever the account is playing, and stops it
at the end.

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
| `--bench-real` | Real data. Required without `--fake`, and itself requires `--profile`. |
| `--bench-gpu-passes` | Turn the pass-granular GPU timeline on for the run (per-pass ms in the GPU stream). Off by default: it adds timestamp queries at every pass boundary; the run then also measures that cost (`gpu-pass-overhead`). |
| `--bench-hide-cycles N` / `--bench-hidden-sec S` | `hide-restore` only: cycles per mode (1–100, default 3) and seconds hidden per minimize / hide cycle (3–900, default 30). |
| `--bench-uris k=uri,...` | Explicit targets: `track`, `lyrics` (word-synced), `lyrics-line`, `album`, `playlist`, `artist`, `big` (`liked` = Liked Songs). |
| `--bench-label NAME` | Stored in the summary (`cold`, `warm`, a branch name). |
| `--probe-out DIR` | Where everything is written (default `<profile>\bench`). |
| `--fake-video PATH` | With `--fake`: enables the `video` scenario. |
| `--bench-shots` | `sidebar-disclosure` / `drawer-toggle` only: before the measured window, two toggles from rest with every presented frame captured for 0.6 s into `shots\<scenario>-<toggle>-<frame>-<ms>.png`. A capture stalls the GPU for its turn, so the frames are spaced wider than the display's; the motion is time-based, so each is the true pose at its moment. |

Real targets come from the profile itself: `WaveeMusic\play-recency.json` (what was played, newest first) and
`WaveeMusic\history.json` (where the user navigated), unless `--bench-uris` names them. The lyrics scenarios ask the
lyrics store for the candidates' documents *without playing them* and take the first word-synced (`Syllable`) and the
first line-synced one.

The window is made topmost for the run: a covered window stands its presents down, which would measure the stand-down.
The full suite at the default windows takes about 3–4 minutes.

## Scenarios

| Name | What runs |
|---|---|
| `idle` | Nothing playing, Home. Before its window the loop must go quiet (two seconds of ≤ 2 presents each, up to 20 s — image loads, model loads, the post-navigation settle); `settleSec` says how long that took, and the wake census says what still woke it. |
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
| `gpu-pass-overhead` | `--bench-gpu-passes` only: the same A/B with pass timing off/on (GPU ms per frame and CPU, ledger on throughout). |
| `sidebar-disclosure` | Opt-in, by name only: the Classic sidebar's Playlists section collapsing and expanding every 0.6 s (the reveal band in a virtual list). Skipped when no sidebar pane is mounted. Also writes `sidebar-disclosure-pose.csv`: the band's presented extent after every UI frame, which shows whether the pose advances every frame (the present count alone cannot). |
| `drawer-toggle` | Opt-in, by name only: the first bench playlist, its third row's drawer opening and closing every 0.6 s (a `FlowReveal` drawer in the track table). |
| `hide-restore` | Opt-in, by name only (it minimizes and hides the window): minimize, tray-hide and cover cycles, memory sampled visible / hidden / restored plus the restore latency. See `docs/guide/hidden-memory.md` and `ops/tools/hidden-mem.ps1`. |

The scroll scenarios first wait (Home 20 s, the list 30 s) for a scroller whose content is at least three viewports long —
among the vertical viewports at least 30 % the area of the largest, the one with the longest content (on a list page the list,
never the sidebar) — and are **skipped with what they found** if none appears, instead of measuring a page that cannot move.
`scrollViewports` / `scrollSteps` and the note say how far the window actually scrolled.

Each scenario sets itself up, runs the warm-up, then the measured window. The loop is FluentApp's own (`RunFrame`,
`TickDetachedHosts`, `WaitRequestWithDetached` → `WaitForWork` → `NoteLoopWait`); only the wait's timeout is capped so the
next scripted action and the window's end land on time (the ledger records the loop's own request, not the cap).

## What it writes

Per scenario: `<name>-ui.csv`, `-render.csv`, `-gpu.csv`, `-memory.csv`, `-audio.csv` (one row per record, times in ms since
the window start) and `<name>.fgl` (the binary ledger; `LedgerSnapshot.ReadFile`). Then `frame-bench-summary.json`,
`frame-bench-summary.txt` and the same table on stdout:

```
scenario          pres/s  pdP50  pdP99  rdP50  rdP99    uiC    rdC   othC  proc%  gpP50  gpP99 gpBsy% miss/s alloc/f   wsMB vramMB  gc0/s
```

## The streams (the ledger)

| Stream | One record per | Key fields |
|---|---|---|
| UI | `AppHost.RunFrame`, early-outs included | entry / pump / Paint's six phases / return stamps; the loop wait before it (+ kind); `exit` (Painted, Idle, Gated, Parked, VideoOnly, ...); the frame's `WakeReasons` (`wakeMask`); UI-thread CPU (cycles, and GetThreadTimes); allocated bytes; GC counts and pause; the process's cumulative cycles; `publishSeq`; nodes, slices, components, damage coverage |
| render | render-thread turn | wait start / turn start / slot open / present done / end; `kind` (Fresh, Motion, TickSpent, CatchUp, Bare); `outcome` (Recorded, CompositeOnly, Elided, SkipSubmit); the present split (stage/record/submit/fence/latency/present/video ms); render-thread CPU (cycles, and GetThreadTimes); allocations; `publishSeq` (joins UI); `submitSeq` (joins GPU; 0 unless the turn submitted); tiles rastered; damage coverage; missed ticks |
| GPU | retired whole-frame timestamp pair | GPU ms; start/end on the QPC timeline (`GetClockCalibration`); `submitSeq`; per-pass ms (uploads, baked blur, clear, scene, glyph band, tile raster, offscreen, composite) when pass timing is on |
| memory | 250 ms (own thread) | working set, private bytes, managed, GC heap/committed, VRAM local/non-local, image cache, glyph atlas, process CPU (GetProcessTimes) and cycles |
| audio | 250 ms | device underruns (the sink's own `IBufferedAudioSink.DeviceUnderruns` decision — its queue was empty at a write on a running stream after a full buffer had gone through), app xruns (the feed ring ran empty — producer side), the window's minimum device padding |

`--fg ledger` (or `--fg ledger=PATH`) turns the same ledger on in an ordinary run; with a path, the binary dump and the
five CSVs are written there when the window closes.

## The summary's fields

Per scenario, `metrics` is a flat map. `X.avg/.p50/.p95/.p99/.max` are distributions (nearest rank). Every total is per
second, so windows of different lengths compare.

| Field | Meaning |
|---|---|
| `presentsPerSec`, `presents` | Render turns that called Present on the main swapchain. 120 Hz caps it at ~120. |
| `framesPerSec`, `paintedFramesPerSec`, `frames`, `paintedFrames` | `RunFrame` calls (incl. early-outs) and the ones that ran Paint. Neutral in a compare: more frames is not better or worse by itself. |
| `exitPerSec.*` | The gate each RunFrame left by (Painted, Idle, Gated, Parked, VideoOnly, ...). |
| `wakePerSec.*` | The wake census: frames per second whose idle decision saw that `WakeReasons` bit (what kept the loop awake). |
| `turnPerSec.*` | Render turns per second by kind (Fresh = a UI publication, Motion = render-side motion re-posed the retained scene). |
| `presentIntervalMs.*` | Present-to-present time: the smoothness number (8.33 ms at 120 Hz). |
| `paintedCpuMs.*` | UI-thread CPU per **painted** frame — the frame-cost number. `uiCpuMs.*` blends in the early-outs. |
| `uiFrameMs.*`, `paintedFrameMs.*` | Wall time per RunFrame / per painted frame. |
| `renderCpuMs.*`, `renderTurnMs.*` | Render-thread CPU / wall per presented turn. |
| `otherCpuMs.*` | Per UI frame interval: process CPU − UI − the render turns' share of that interval (by time overlap). Unclamped, so the mean is unbiased; one interval can read slightly negative where counter reads skew. |
| `uiCores`, `renderCores`, `otherCores`, `processCores` | Average cores over the window from cycle counts; the first three add up to the fourth. |
| `uiCoresTimes`, `renderCoresTimes` | The same two threads from GetThreadTimes — no rate involved, the cross-check. |
| `processCpuPct` | **The headline process CPU**: GetProcessTimes over the window / (wall × processors), Task-Manager style. `processCpuPctCycles` is the same from cycles. |
| `cyclesPerMs.window` | The counter's effective rate inside this window alone (diagnostic; the run uses ONE rate, the JSON's top-level `cyclesPerMs`). |
| `gpuMs.*`, `gpuBusyPct`, `gpuPass.*Ms` | GPU execution per frame; Σ GPU ms / wall; average per-pass time over frames with a pass timeline (needs `--bench-gpu-passes`). |
| `uiAllocBytesPerFrame`, `paintedAllocBytesPerFrame`, `renderAllocBytesPerTurn`, `processAllocBytesPerSec` | Managed allocations. |
| `gc0PerSec`, `gc1PerSec`, `gc2PerSec`, `gcPauseMsPerSec` | Collections and pause time per second. |
| `missedVsyncsPerSec`, `missedTicksPerSec`, `gpuMissedSamplesPerSec` | The host's missed-vsync counter; compositor ticks a live-motion present skipped; GPU samples the ledger could not see. |
| `workingSetMB`, `privateMB`, `managedMB`, `gcHeapMB`, `vramLocalMB`, `vramNonLocalMB`, `imageCacheMB`, `glyphAtlasMB` (`.avg`, `.peak`) | Memory. On a UMA iGPU (Adreno) VRAM LOCAL is system memory and is inside the working set. |
| `audioDeviceUnderrunsPerSec`, `audioXrunsPerSec`, `audioPaddingMinMs` | Audio health (none under `--fake`: its endpoint is silent). An underrun without an xrun is downstream of the app's ring (a late RT write, an audiodg stall). |
| `settleSec` (idle), `scrollViewports`, `scrollSteps` (scroll scenarios) | Scenario-specific, described above. |

`ledger-overhead` and `gpu-pass-overhead` report `framesPerSec.off/on`, `uiCpuUsPerFrame.off/on`, `runFrameWallUs.off/on`,
`processCpuPct.off/on`, `gpuMs.off/on` and their differences (`overhead*`), measured around the loop itself — the ON figure
includes everything the switch does.

### How CPU is measured

Per-thread CPU is `QueryThreadCycleTime`, the process total `QueryProcessCycleTime`; both are cycle counts. On a DVFS core
(Snapdragon X) the counter follows the clock, so cycles are converted at the run's **effective** rate: the process's cycles
per millisecond of process CPU time (Δ QueryProcessCycleTime / Δ GetProcessTimes) over the whole run, one rate for every
window of the run, stored as `cyclesPerMs` in the summary. Two runs whose rates differ by more than 1 % make the compare tool
warn: their cycle figures are then on different scales, while `processCpuPct` and the `*CoresTimes` figures (scheduler-tick
accounting, exact over seconds) need no rate at all.

### Measuring CPU: cycles, not time

The CPU headline is **raw cycle counts**, not CPU time. `GetProcessTimes` / `GetThreadTimes` charge a thread in whole scheduler
ticks (about 15.6 ms): a thread that runs 0.3 ms per frame is billed 0 or 15.6 ms depending on whether a tick happens to land
inside it. At low load the totals therefore fall into two modes (one build read 0.45 % and 2.06 % for identical cycle counts;
the implied clock ranged 0.7 to 16 GHz), and the per-run `cyclesPerMs`, derived from that time, inherits the noise. The cycle
counters (`QueryProcessCycleTime`, `QueryThreadCycleTime`) are exact per instruction and repeat to about 3 %.

Every scenario therefore carries rate-free metrics, computed from raw cycle deltas with no conversion:
`processGcyclesPerSec`, `uiMcyclesPerSec`, `renderMcyclesPerSec`, `otherMcyclesPerSec` (process - UI - render),
`uiKcyclesPerPaintedFrame` (all UI-thread cycles in the window / painted frames) and `renderKcyclesPerTurn`. The stdout table
shows them (`procGc/s`, `uiMc/s`, `rdMc/s`, `othMc/s`). `processCpuPct`, `uiCoresTimes`, `renderCoresTimes` and `paintedCpuMs.*`
stay in the JSON (schema `wavee-frame-bench/2.1`) as **info only**: the compare tool never flags them. Cycles are not
frequency-normalised, so compare runs on the same machine and power plan.

## Comparing

```powershell
powershell -File ops\tools\frame-bench-compare.ps1 -Before C:\scratch\bench\base -After C:\scratch\bench\lane-a
powershell -File ops\tools\frame-bench-compare.ps1 C:\scratch\bench\real-cold C:\scratch\bench\real-warm -All -Threshold 5
```

Each row: before, after, delta, % change and `REGRESSED` / `improved` when the change passes `-Threshold` (default 10 %; the cycle metrics use `-CycleThreshold`, default 5 %, with floors 0.01 Gcycles/s, 2 Mcycles/s, 10 Kcycles)
and the metric's absolute floor (0.05 ms/us/cores, 1 MB, 1 %, 64 B, 0.1 per second, 1 count). Direction: presents per second
and the audio padding minimum are higher-is-better, run-describing figures (frame rates, the census, the A/B arms, scroll
distance) are neutral, everything else is lower-is-better. `-FailOnRegression` exits 1 on a regression; `-Scenario` narrows;
`-All` prints every metric. It warns before comparing when the runs differ in data (fake/real), window, processors, measure or
warm-up window, pass timing, or cycle rate. Its own tests: `powershell -File ops\tools\frame-bench-compare.tests.ps1`.

Run-to-run noise on a busy machine is easily 5–10 % for p99 figures: compare medians first, repeat a run that flags
something before believing it, and never compare a fake run with a real one.

## Source

- Engine: `FluentGpu.Engine/Hosting/FrameLedger*.cs`, `AppHost.Ledger.cs`, `Media/Playback/Audio/AudioHealth.cs`;
  Windows seams `FluentGpu.Windows/Pal/Win32LedgerSampler.cs`. Tests: `FluentGpu.Engine.Tests/FrameLedgerTests.cs`.
- App: `src/apps/Wavee/Screens/Diagnostics.Probe.FrameBench.cs` (the arm), `Diagnostics.FrameBench.cs` (pure: options,
  targets, summary maths, JSON, table). Tests: `src/apps/Wavee.Tests/FrameBenchTests.cs`.
- Compare: `ops/tools/frame-bench-compare.ps1`, tests `ops/tools/frame-bench-compare.tests.ps1`.
