# Handoff — idle GPU, continued (2026-09-12, second session)

**Everything below is UNCOMMITTED** in both repos (~18 files in `..\fluent-gpu`, 13 in `C:\wavee\waveemusic`).
Run `git status` in both first. Do not redo this work.

## Where it actually landed

Idle GPU went **54.8–69 % → 0.00 %**. Measured on the arm64 AOT build, fully settled, 10 s window:

```
GPU 3d : mean=0%   max=0%      (0.00% minimized; single instance phys_0_eng_0_engtype_3d, no double-counting)
CPU    : 0.009%    (12 cores)
Memory : ws=493 MB
```

The headline fix was **the σ>0 blur veto**, not a leaked frame-clock subscriber. See "Corrections" — the premise the
previous handoff was built on is wrong.

## What is done and green

**Engine (`..\fluent-gpu`)**
- **σ>0 blur admitted to clamped replay** (`damage-scoped-repaint-design.md` §2a/§2b + §2c items 1–4). A blurred
  group's source renders over `R ⊕ TapRadius(σ)` and composites only `R`. The halo is DERIVED from the open-group
  stack (`_layerHaloPx`, recomputed at the 7 places `_opacityGroups` changes) — the PushLayer arm has eight early
  `continue` paths and a hand-paired counter leaks on whichever one you miss. PopLayer's existing order already drops
  it before `BlurInPlace` and every composite. Plus `EnclosingBlurHaloDip` (§2b, recorder-side ancestor halo) and a
  refusal to mint a blur pin from a clamped frame (`ReplayCoversRegion`). `RepaintStreamSafety.Scan` now vetoes only
  Acrylic and `PushStencilClip`.
- **Two instruments**, both always-on, appended to the 30 s `[wake]` line:
  - `pollers=N:Name` — live `FrameClock.Tick` subscribers by owning component type (`Computation.DiagOwner`).
  - `anim=N:Channel*k done=D parked=P loop=L orphans=N` — live compositor tracks by channel + scene orphans.
    This one exists because **`WakeReasons.Anim` and `WakeReasons.Orphans` are MASKED while
    `RenderOwnsCompositor`** (`AppHost.cs:2268,2273,2274`), so the census was structurally blind to exactly the rows
    that cost the most: a pinned page printed `kept: frameNeeded timer` and nothing else.
- Gates: `gate.frameclock.poller-census`, `gate.anim.flip-cell-idles`; the two stream-safety gates rewritten to the
  new contract. §13.1a canon row + `RepaintStreamSafety` XML contract updated; `check-canon.ps1` clean.

**App (`C:\wavee\waveemusic`)** — a separate, real bug found on the way:
- `DeepLinkParse.ReadQuery` trimmed only the RAW uri, so an encoded `%20` decoded into a trailing space that rode into
  `spotify:playlist:<id> `, Spotify 400'd, and the rows region had `Failed: static () => false, OnFailed: null` — **no
  representation for failure at all**, so it shimmered for 8m35s. Fixed both halves: trim after unescaping, and a real
  `PlaylistRowsState.Failed` with a Retry (store failure flag → `LibrarySync` catch → `DetailModel` → `SkelRegionEl`).
  Rows always beat a failure so a dead revalidate never blanks a populated list.

**Verification at the time of writing**
| gate | result |
|---|---|
| Engine Debug / Release | 0 errors |
| `FluentGpu.Engine.Tests` / `Windows.Tests` | 307/307, 227/227 |
| VerticalSlice Debug | clean on 2 consecutive runs |
| VerticalSlice **Release** | last run printed **1518** — but that binary was STALE (built before `gate.anim.flip-cell-idles`). **Rebuild Release and confirm 1519.** |
| `--repaint-identity` (Adreno X1-85) | **8/8 pixel-identical**, incl. new `blur-group-straddle` taking the partial route (`rects=2`) |
| negative control | halo forced to 0 ⇒ that scenario fails by **10 321 px** over 565×163 — the scenario really bites |
| App Debug / Release, `Wavee.Tests` | 0 errors, 8016/8016 |

Flaky, pre-existing, not yours: the `gate.touch*.alloc-zero` family fails ~1 run in 4 (different member each time) and
fails on an untouched tree too. `Wavee.Tests` failed 1 test once in 7 runs and would not reproduce.

## THE OPEN BUG — a reclaim deadlock (strong lead, not yet fixed)

Symptom seen live: a settled page pinned at **`animTracks=4 orphans=1` on every sample for minutes**, GPU flat at
~10 %, while the UI loop was asleep at `fps=2.5`. `anim=4:TranslateY*2,Opacity*2` — exactly one digit cell's
`Enter(Dy,Opacity)` + `Exit(Dy,Opacity)`, i.e. the `FlipCountdown` daylist clock (`Components/FlipCountdown.cs`,
a keyed child remounting once per second, 150 ms `MotionTok.ControlFast`).

**The mechanism, two lines:**
- `AppHost.cs:2274` — `if (!_anim.RenderOwnsCompositor && _scene.OrphanCount > 0) r |= WakeReasons.Orphans;`
  On the production path the render thread owns the compositor, so an outstanding orphan requests **no frame**.
- `AppHost.cs:3672` — `ReclaimSettledOrphans()` runs **only inside a UI frame**.

So the only thing that can clear the orphan is a frame, and the orphan is not allowed to ask for one. It stays live,
its exit tracks stay live, `HasRenderMotion` stays true, and the render thread presents every vblank forever.

**Why the headless gate passes:** headless has no render thread ⇒ `RenderOwnsCompositor == false` ⇒ the mask is off ⇒
the wake fires ⇒ reclaim runs. `gate.anim.flip-cell-idles` therefore passes and proves nothing about production. **A
gate that reproduces this must exercise the render-owned path**, and must remount repeatedly (once per second), not
once — the existing gate flips the key a single time.

**Proposed fix (unverified):** fire the Orphans wake reason when an orphan is *reclaimable* — no tracks, or its
deadline passed — regardless of ownership. `OrphanCount` is 0–2 in practice so the walk is free, and it keeps the loop
asleep while an exit is genuinely animating, waking exactly once when there is something to retire.

**Unresolved sub-question:** 4 tracks persisted, but only 2 (the exit pair) live on the orphan. `CollectAndFreeDone`
runs inside `_anim.Tick`, which under render-ownership runs on the render thread, so the enter pair *should* retire on
its own. Explain the other 2 before declaring it fixed.

**Reproducing it is the hard part.** It is NOT currently reproducible: the daylist countdown window rotated out, so
`FlipCountdown` renders nothing (`top=TitleBar`, `anim=0`). Either wait for a live daylist window, or synthesise the
shape — a component remounting a keyed `Enter`/`Exit` child every second — against a real render thread.

## Corrections — do not re-chase these

1. **There is no leaked `FrameClock.Tick` subscriber.** The previous handoff's whole premise. Both logged sessions show
   `lyrics.clock` at 3600 frames/30 s with `zeroAdvanceFrames=0` to the last line — playback was LIVE for the entire
   window that was called idle — and `LyricsTicker` is gated on `open` (`LyricsView.cs:446`), so the stepper was doing
   its job. `pollers=0` on the new build confirms it.
2. **The countdown flip does not wedge in isolation** — `gate.anim.flip-cell-idles` passes (tracks 0, orphans 0, idle in
   10 frames). The wedge needs the render-owned path.
3. **It is not page-dependent** — 0.00 % on home and on the playlist.
4. **A 9.4 % reading ~80 s after launch was STARTUP SETTLING**, not idle (that window carried `imageReady=231`). The
   loop quieting to `fps=2.5` is not sufficient evidence of idle; wait for GPU to actually flatten.

## Operational notes

- **Never round-trip a source file through PowerShell 5.1 `Get-Content`/`Set-Content`.** It decodes as Windows-1252 and
  mojibakes every non-ASCII character — it silently corrupted 340 comment lines in `D3D12Device.cs` here (builds, tests
  and the pixel probe all still passed). Symptom: `git diff --numstat` far larger than the lines you touched. Recovery
  is exact: `open(p,'rb').read().decode('utf-8').encode('cp1252')`, then re-add the BOM. Python must use
  `encoding='utf-8-sig'` on read AND write, or it drops the BOM. **Check `git diff --numstat` after any scripted edit.**
- AOT publish: `powershell -File ops\build\publish-wavee-aot.ps1 -Arch arm64` **from the PowerShell tool** (~2.5 min,
  43 MB). The script's own comment explains why: an emulated shell reporting x64 silently produces a win-x64 build.
  The running app locks the output — close it first.
- **Driving the app works**: `WM_COPYDATA` (0x004A) to the `FluentGpuWindow` HWND, `dwData=0x46474143`, UTF-16 payload.
  `FindWindowEx` failed here; use `Process.MainWindowHandle` directly. `spotify:playlist:<id>` opens a page;
  `wavee://open?route=home` goes home.
- **GPU counters**: `\GPU Engine(pid_<pid>*engtype_3d)\Utilization Percentage`. 14 instances per pid on this box but only
  `phys_0_eng_0` is ever non-zero, so summing is safe — verify per-instance before trusting a total. Sample ≥8 times;
  a flat trace means a continuously-armed clock, a bursty one means a duty cycle.
- `[wake]` prints every 30 s. `frame.slow` is rate-limited and biased toward slow frames — not how to read steady state.
- In the `[wake]` census, **`rendered` means reconciled-or-laid-out, NOT presented.** There is no present counter in it.
  Use `nav.frames presented=` (4 s after a route change) or `PresentedSequence`/`FramesSkippedSubmit`.

## Not done

- §2c items 5 and 7: the headless `ReplayLayered` CPU reference still has no Gaussian (so σ>0 is gated on-device only),
  and the perf-only narrowing of the blur source. Item 7 is explicitly risky (breaks pin size-exactness).
- The CHANGELOG bullet for the playlist-skeleton fix has **no `(#n)`** — opening an issue needs the user's approval.
  `[Unreleased]` is not gated, but the ref is required before that heading is renamed for a release.
- Nothing is committed.
