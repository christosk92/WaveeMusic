# Universal GPU default — Get-Counter recipes

Continuous per-PID GPU timelines (never Task Manager’s 1 s smear). Run while Wavee is open; filter to the Wavee PID.

## Playing spike (A1)

```powershell
# Terminal A — start Wavee with wake/fps diagnostics
$env:FG_WAKE_DIAG = '1'
$env:FG_FPS_LOG = '1'
# optional: $env:FG_GPU_TIMING = '1'   # after mid-stamp + freshness fix

dotnet run --project src/apps/Wavee
```

```powershell
# Terminal B — continuous engtype_3d for the Wavee PID
$pid = (Get-Process Wavee -ErrorAction SilentlyContinue | Select-Object -First 1).Id
if (-not $pid) { throw 'Wavee not running' }
Get-Counter '\GPU Engine(*)\Utilization Percentage' -Continuous |
  Where-Object {
    $_.CounterSamples |
      Where-Object { $_.InstanceName -match "pid_${pid}" -and $_.InstanceName -match 'engtype_3d' -and $_.CookedValue -gt 0 }
  } |
  ForEach-Object {
    $s = $_.CounterSamples | Where-Object { $_.InstanceName -match "pid_${pid}" -and $_.InstanceName -match 'engtype_3d' }
    '{0:HH:mm:ss.fff}  {1:n1}%' -f $_.Timestamp, ($s | Measure-Object CookedValue -Sum).Sum
  }
```

**Expect after A1:** paused ~0%; playing + EQ visible ~8–15% (not ~56%). `[fps]` wait token ambient-class; no `sole: frameClockPoller` with lyrics closed.

## Scroll (B4 → B3)

Same counter recipe while scrolling the artist/album detail list maximized.

| Milestone | Scroll `engtype_3d` (order-of-magnitude) |
|---|---|
| Before | ~50–63% |
| +B4 opaque content | ~30–40% |
| +B3 occlusion inset | ~15–25% |

**B4 eyeball:** content plate opaque; titlebar/sidebar/player bar still show live Mica; TL corner cut-away preserved.

## Reading counters across §5.1 (damage-scissored partial repaint)

§5.1 Phase B changed what a per-frame device counter MEANS, so any recipe comparing pre-/post-§5.1 numbers has to
account for two effects or it will read a win as a regression.

**1. Per-frame counters multiply by the replay count.** A partial frame walks the DrawList once per replay rect, so
`rects`, `glyphInstances`, `scissorSets`, `segments` and `imagesSkipped` count each surviving primitive once PER RECT.
A 3-rect frame can report ~3× the instances of the equivalent pre-§5.1 frame while doing a fraction of the fill — the
point of the feature is that decode-time culling drops nearly everything outside each rect, which these counters do not
show. Read them against `dmgReplayRects`, never on their own.

**2. The `dmg` tokens are the actual §5.1 readout.** On the `[fps]` line: `dmgRoute` (0 FullDirect / 1 FullIntoCanvas /
2 Partial), `dmgPartialFrames` vs `dmgFullFrames` (the ratio that says whether the feature engages at all),
`dmgReplayRects`, `dmgCoveragePct`, and `dmgFullReason` — why a frame surrendered. `EmptyDamageStreamMismatch` there
means a patch is changing DrawList bytes without dirtying a node; `PublishGap` means the publisher's carry did not
cover a dropped frame. A capture with **no `dmg` token at all** means the build predates Phase B, not that the path is
idle.

**3. `dmgPublishGap` is attribution, not an error.** DropOldest makes sequence gaps normal under exactly the load this
feature exists for, and the publisher unions the dropped damage forward. The counter says a gap happened, not that
anything was lost.

**4. The §5.1 SPEED-UP IS STILL UNMEASURED — this is the recipe that closes it.** No multiplier is claimed anywhere in
the canon or the reports, deliberately: the floor is the full-surface canvas blit, and on a tiler the saving also
depends on whether the driver skips tiles behind a `ClearRenderTargetView` with `NumRects > 0` on a retained RT — which
D3D12 cannot *declare* without `BeginRenderPass`. The modelled ≈3.7× and the pessimistic ≈1.9× are both defensible
arithmetic; only the device can decide between them. To settle it, at ONE pinned window size, settled, playing,
untouched:

```powershell
Get-Process Wavee -EA SilentlyContinue | Stop-Process -Force
$env:FG_GPU_TIMING='1'; $env:FG_FPS_LOG='1'
# ...launch, discard the first ~8 s, then read from the [fps] line:
#   dmgRoute / dmgPartialFrames vs dmgFullFrames   → is the partial path even engaging?
#   the CatComposite GPU slice vs the scene slice  → how much of the frame is the blit floor?
#   per-PID engtype_3d via Get-Counter (never Task Manager)
```

Compare a **playing + EQ** arm (should be mostly `dmgRoute 2`, low `dmgCoveragePct`) against a **scroll** arm (should be
mostly `dmgRoute 0` — the fallback is today's path verbatim, so scroll must be UNCHANGED, and a scroll regression is the
first thing to look for). The same run also settles `RepaintPolicy.CoverageCutoff`'s 60 %, which is a chosen constant,
not a measured one; near-cutoff hysteresis is deliberately not implemented until this measurement exists, because
guessing a second constant on top of the first is how the first one stopped being questioned.

## Gates (agent)

```powershell
dotnet build src/FluentGpu.slnx
dotnet build src/FluentGpu.slnx -c Release
dotnet run --project src/FluentGpu.VerticalSlice
dotnet run --project src/FluentGpu.WindowsApp -- --repaint-identity   # GPU: 6/6 partial-vs-full pixel identity
```
