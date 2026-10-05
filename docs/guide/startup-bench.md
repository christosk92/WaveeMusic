# Wavee startup bench

Report-only probe for process-start → first-present → session-restored timings. **Not a CI gate.**

## Trigger

```powershell
dotnet run --project src/apps/Wavee -- --startup-bench --fake [--probe-out <dir>]
```

A command-line flag only — there are **no environment variables** (0.2.9's `WAVEE_STARTUP_BENCH`, `WAVEE_PERF_BENCH` and
`WAVEE_BENCH_OUT` are gone; `ProbeOptions.Parse` in `Screens/Diagnostics.Probe.Arms.cs` reads the flags).

`--probe-out <dir>` (default `<LocalFolder>\bench` — the profile directory under `--profile`)
receives `wavee-startup-latest.json` / `.txt` and a timestamped copy.

`--fake` keeps the run offline (no login/network). The probe takes over the frame loop via `FluentApp.DiagnosticRun` and
exits after the report. For per-frame CPU/GPU/memory use `--frame-bench` instead ([frame-bench.md](frame-bench.md)).

## Timing definitions

| Mark | Clock | Meaning |
|---|---|---|
| **process-start** | `Process.GetCurrentProcess().StartTime` | OS process creation (includes runtime init, same anchor as FluentApp `[boot] runcore-entry: sinceProcessStart`). |
| **first-present** | `D3D12Device.FirstPresentQpc` | First **successful** `IDXGISwapChain::Present` on the render thread. Fallback if that stamp is still 0: first pumped frame with `AppHost.LastStats.Presented`. |
| **session-restored** | first frame after `NavigationFrameWatch.NavigationId > 0` | The content host activated its first route (the shell mounted and restored its navigation). The probe does **not** timestamp the restore call itself. Treat this as “shell mounted and session-nav apply has been attempted,” not “every restored route’s data is on screen.” |

`DiagnosticRun` fires after `window.Show()` and **before** the first frame . The probe pumps frames (latency-wait + vsync suppressed) until both marks land or 1200 frames elapse.

## GPU vs working set

iGPU D3D12 resources live in shared system memory and inflate `WorkingSet64`. The render thread snapshots `IDXGIAdapter3::QueryVideoMemoryInfo` into `GpuVideoMemorySnapshot` (`D3D12Device.LastVideoMemory`). About and this probe read that cold struct only.

- **LOCAL** = adapter-local (VRAM on discrete, system RAM on UMA/iGPU).
- **NON_LOCAL** = the other segment (system-memory overlap on discrete; usually empty on UMA).

Derived “app memory excl. GPU assets” ≈ working set − the shared-segment usage (LOCAL on iGPU/UMA, NON_LOCAL on discrete). Settings → About shows the same split.

## Chain

`Diagnostics.Probe.InstallGuiArms` (`Screens/Diagnostics.Probe.Arms.cs`) owns `FluentApp.DiagnosticRun`:

`TryStartupBench || TryPerfBench || TryFrameBench || TryMenuBench || TryLyricsAdvanceProbe || TryLyricsDemo`

Returning `true` skips the interactive loop (FluentApp diagnostic-harness contract).

## Source

- Probe: `src/apps/Wavee/Screens/Diagnostics.Probe.Arms.cs` (`TryStartupBench`; its pure JSON helpers are `Diagnostics.Bench`)
- Snapshot: `GpuVideoMemorySnapshot` in `FluentGpu.Windows/D3D12/D3D12MemoryDiagnostics.cs` (engine), published from the render thread
