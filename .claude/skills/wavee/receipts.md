---
name: receipts
description: Wavee performance receipts — Settings → About "Wavee right now", GPU-vs-app working-set split, and the --startup-bench probe.
---

# Wavee performance receipts

Settings → About shows a **cold 5s** snapshot of process memory, FPS, and a GPU-vs-app split. Do not turn this into a per-frame HUD.

## About — "Wavee right now"

File: `src/apps/Wavee/Features/Shell/SettingsPage.About.cs` (`WaveeNowReceipts`).

| Field | Source |
|---|---|
| Working set | `Process.GetCurrentProcess().WorkingSet64` |
| Managed heap | `GC.GetTotalMemory(false)` |
| Uptime | `DateTime.Now - Process.StartTime` |
| FPS | `WaveeStartupBench.Host.LastStats.Fps` (stashed in `DiagnosticRun`, **not** `FrameDiagnostics.Current`) |
| GPU assets | `D3D12Device.LastVideoMemory` LOCAL + NON_LOCAL `CurrentUsage` |
| App excl. GPU | working set − shared-segment usage, labeled `shared / iGPU` or `discrete` |

**Refresh:** `UseInterval(Tick, 5000)` + mount `UseEffect(Tick)`. Format strings **on the tick only**. Bind `TextEl(signal)` so the parent `Render()` stays run-once — never `UseContext(FrameDiagnostics.Current)` (that re-renders every frame; see `FpsOverlay`).

`AboutTab` is behind SettingsPage’s tab switch, so the interval **must** live on this child (`Embed.Comp(() => new WaveeNowReceipts())`), not in `AboutTab()` itself.

Tokens only: `Tok.*`, `Spacing.*`.

## GPU snapshot (render thread owns COM)

`IDXGIAdapter3*` lives on `D3D12Device` (QI via `EnumAdapterByLuid` on first Present). `QueryVideoMemoryInfo` runs on the **render thread** inside `Present`. Values copy into `GpuVideoMemorySnapshot` (`D3D12MemoryDiagnostics.PublishVideoMemory`). UI reads `D3D12Device.LastVideoMemory` — never the COM pointer.

Do not add this to `IGpuDevice` / PAL unless you also reconcile `docs/design/subsystems/pal-rhi.md` + `gpu-renderer.md` and run `check-canon.ps1`. The numeric struct on `D3D12Device` is the intended surface.

iGPU/UMA: LOCAL is shared system RAM and **is** in the working set. Discrete: LOCAL is VRAM (not in WS); NON_LOCAL is the WS overlap. `GpuProfile.IsWeak` forces the iGPU label.

`D3D12MemoryDiagnostics.Track` / `Release` stay untouched; the snapshot only **reads** `LiveTotals()`.

## Startup bench

`docs/guide/startup-bench.md`. Trigger: `--startup-bench` and/or `WAVEE_STARTUP_BENCH=1`. Chain front in `Program.cs` `DiagnosticRun`. Report-only.

**session-restored** = first frame after `WaveeShell.ProbeNav != null` (restore runs at that mount). Do not edit `WaveeShell.cs` to add a timestamp.
