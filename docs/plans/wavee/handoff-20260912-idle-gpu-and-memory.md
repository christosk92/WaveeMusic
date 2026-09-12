# Handoff — idle GPU and memory on the Snapdragon (2026-09-12)

Paste the block at the bottom as the opening message of a fresh session. Everything above it is the detail that
block refers to.

---

## Where things stand

Fourteen commits landed across both repos, all gates green, nothing uncommitted:

- engine `..\fluent-gpu` — 11 commits, `876fe994c..419bdc626`
- app `C:\wavee\waveemusic` — 3 commits, `b3f6647a..7cef3a0e`

Verification at HEAD: VerticalSlice **1517** checks, `FluentGpu.Engine.Tests` **307**, `FluentGpu.Windows.Tests`
**227**, `Wavee.Tests` **8012**, Debug **and** Release clean in both repos, `check-canon.ps1` clean,
`--repaint-identity` **7/7 pixel-identical on the real Adreno X1-85**.

`gate.arena.alloc-zero` flakes roughly 1 run in 4. It is order-dependent and pre-existing — the slice does not even
reference the assemblies this work touched. Re-run before believing a red suite.

## The one thing that did not work, and why it matters most

**Idle GPU is unchanged.** Measured on the NativeAOT arm64 build, live, signed in:

| state | GPU 3D (this process) |
|---|---|
| before playback started | 54.8 % mean / 70.9 % max |
| playing + lyrics rail open | 69.0 % mean |

The loop never idles: `idleAgo` climbs monotonically past 500 s, `streak=71373`, and
**`frameClockPoller` is set on 100 % of loop runs** (sole reason on 20–46 % of them), with
`run=4150 rendered=179 recordOnly=3960`.

The reason the repaint work did not move this number is worth carrying forward: **it fixed the compositor-pose
path** (the anim slab — what a marquee drives). `FrameClockPoller` is a *different* keepalive — a component
re-rendering every frame through `UseContext(FrameClock.Tick)`. Those publish through the RECONCILER as **fresh**
frames, so neither the `!fresh` record-elision nor the compositor self-epoch damage scoping applies to them. Same
symptom, different mechanism. The original investigation blamed the marquee; on this machine that is the wrong
suspect.

**Not yet isolated: which component holds the subscription.** Every candidate is conditionally mounted *by design*,
so one has a stuck predicate:

| candidate | mount gate |
|---|---|
| `LyricsFrameStepper` (`LyricsView.cs:3128`) | `playing \|\| cascading \|\| followMode != Following` — legitimate while playing, suspicious otherwise |
| `ScrollBarConsciousTicker` (`ScrollBar.cs:561`) | owner unmounts "when everything settles" |
| `ItemsViewDisclosureWatcher` (`ItemsView.cs:1894`) | `PendingDisclosure != null \|\| ActiveDisclosure != null` |
| `TickerClock` reveal ramp (`DetailTracks.cs:1144`) | `Flow.Show(_rampActive)` |

`AppHost.FrameClockPollerCount => _frameClockSig.SubscriberCount` **already exists and is printed nowhere.**
Surfacing it — ideally with the subscriber's identity — is the obvious next instrument, and is exactly the move that
paid off for the GPU-memory census. Do that before guessing again.

A pause test to separate "legitimate while playing" from "stuck" was attempted and **did not land** (the Space
keystroke never reached the window), so this is genuinely unresolved rather than dismissed.

Second observation from the same session, possibly related and possibly its own bug: the Q-top 1500 playlist sat
**stuck showing skeleton rows**, and skeleton shimmer is a perpetual 1 s looping opacity animation per row
(`Surfaces.cs:488`). Any "idle" measurement taken on that page is not idle.

## What the instruments answered on the first live run

These are settled; do not re-derive them.

- **The UMA thumbnail atlas is refused at CREATE, not at map:**
  `[d3d12] UMA atlas pages unavailable stage=create hr=0x80070057 (ROW_MAJOR CPU-writable TEXTURE2D)`.
  `0x80070057` is E_INVALIDARG. That kills the cheap `WriteToSubresource`-on-the-same-page fallback (which only
  applies to a *map* refusal). ROW_MAJOR `TEXTURE2D` is only guaranteed for cross-adapter resources and has **no cap
  bit**, so probe-and-catch is the only available shape. Remaining options are a 512² retry, an RGBA8 retry, or
  accepting the private-texture fallback. Note the atlas is worth little here anyway: 76 × 64px textures = 4.8 MB
  against ~4 MB for one page, and packing bucket 128 would be a ~31 % byte *regression*.
- **232 MB of GPU memory is invisible to the engine:** `vram=used:325.5/budget:15394.5/tracked:93.5/untracked:232.0`.
  No video was playing, so this is driver arenas, PSO/shader ISA, command allocators and DComp — not MF surfaces.
- Per-bucket image classes work: `Image.Texture.Uma.64x64:4.8/76`, `Image.Texture.Uma.128x128:1.6/25`.
- The owner registry works: `video player=0 src=- state=Idle disposeQ=0`.
- The machine runs at **dpi=144 (150 %)**, which confirms the decode-grid finding — `ImageDecodeScale`'s 8 px grid
  was live, minting a near-unique texture per density step.

## Not done

- **The σ>0 blur half of Step 2.** A lyrics-open frame is still `FullDirect`. Five corrections found while doing the
  rest are recorded in the status block of `..\fluent-gpu\docs\plans\damage-scoped-repaint-design.md` — read it
  before starting, particularly the physical-px vs DIP unit trap and the five unpaired `PushLayer` sub-paths.
- **Scene arrays ×4** (~25–50 MB) and the **16 MiB glyph CPU mirror**. The glyph *staging* banks were done.
- **`ops/tools/gpu-sample.ps1`** and **`ops/tools/native-attribution.ps1`** were specified but never written; the
  session used an ad-hoc harness (see below).
- **The M0–M4 measurement ladder** for the remaining native memory (vmmap type split, the video-off diff, ETW
  `VirtualAlloc` stacks).
- **#118's title/body** still describe only the navigation frame floor. Broadening needs explicit approval — every
  modifying `gh` call does.

## Release bookkeeping — do not miss this

`CHANGELOG.md` has the work under **`## [Unreleased]`**. The release gate
(`ops/release/wavee-release.ps1:509`) reads the `## [<semver>]` section **only**, so it must be renamed to the
version being cut or `issue refs` fails with "no commit carries Fixes #118".

## Operational notes for measuring

- The NativeAOT arm64 build is at
  `src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe`. Rebuild with
  `powershell -File ops\build\publish-wavee-aot.ps1 -Arch arm64` **from the PowerShell tool** (the Bash tool reports
  x64 on this box).
- Launch it **sandbox-free**; the sandbox blocks the GPU. It signs in from the cached account and writes to the real
  `%LOCALAPPDATA%\Wavee` — the 29 MB `library.db`, not a package-local cache.
- Drive navigation with `WM_COPYDATA` (class `FluentGpuWindow`, `dwData=0x46474143`, UTF-16 payload); a bare
  `spotify:track:…` starts playback. A per-navigation process spawn distorts `nav.frames`.
- `--repaint-identity` **runs here sandbox-free** and is the fastest real-pixel check for anything touching damage
  or clamped replay — the headless CPU reference has no Gaussian, no feather and no strip model, so it cannot settle
  those questions.
- `frame.slow` is rate-limited and only fires on SLOW frames, so it is biased and is *not* how to measure a settled
  steady state. Use `[wake]` and the GPU counters.
- GPU counters: `\GPU Engine(pid_<pid>*engtype_3D)\Utilization Percentage` (summed) and
  `\GPU Process Memory(pid_<pid>*)\Total Committed`.

---

## The prompt

> Continue the Wavee idle-GPU and memory work. Read
> `docs/plans/wavee/handoff-20260912-idle-gpu-and-memory.md` first, then
> `docs/plans/wavee/gpu-memory-investigation-2026-09-11.md` (its "Corrections found while implementing" and "What
> landed" sections are current) and `..\fluent-gpu\docs\plans\damage-scoped-repaint-design.md` (its status block
> lists five corrections to the plan).
>
> Fourteen commits already landed and everything is green — do not redo them. The headline problem is NOT solved:
> idle GPU is still 54.8 % with nothing playing and 69 % with lyrics open, because the dominant wake reason is
> `frameClockPoller` (100 % of loop runs), which is a `FrameClock.Tick` subscriber re-rendering every frame — a
> different mechanism from the compositor-pose path that was fixed.
>
> Start by surfacing `AppHost.FrameClockPollerCount` (it exists and is printed nowhere) together with the
> subscriber's identity, so the stuck subscriber can be NAMED rather than guessed. The candidates and their mount
> gates are in the handoff. Then confirm on a live run: launch the arm64 AOT build sandbox-free, and use `[wake]`
> plus the GPU counters — `frame.slow` is rate-limited and biased toward slow frames.
>
> Also check whether the playlist page getting stuck in skeleton rows is a separate bug; skeleton shimmer is a
> perpetual looping animation, so it makes any "idle" measurement on that page meaningless.
>
> Do not re-derive the settled findings listed in the handoff (the atlas E_INVALIDARG, the 232 MB untracked GPU
> figure, the per-bucket census). Before cutting any release, rename the CHANGELOG's `## [Unreleased]` heading to
> the version being cut or the issue-refs gate will fail.
