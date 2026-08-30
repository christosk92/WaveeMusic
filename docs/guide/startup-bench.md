# Wavee startup bench

Report-only probe for process-start → first-present → session-restored timings. **Not a CI gate.**

## Trigger

Either:

```powershell
dotnet run --project src/apps/Wavee -- --startup-bench --fake
```

or set `WAVEE_STARTUP_BENCH=1` (same `Diag.EnvFlag` style as `WAVEE_PERF_BENCH`). `--startup-bench` just sets that env var, matching `--perf-bench`.

Do **not** use `FG_*` names for this probe.

Optional: `WAVEE_BENCH_OUT` (default `%LOCALAPPDATA%\Wavee\bench`) receives `wavee-startup-latest.json` / `.txt`.

`--fake` keeps the run offline (no login/network). The probe takes over the frame loop via `FluentApp.DiagnosticRun` and exits after the report, like `WaveePerfBench`.

## Timing definitions

| Mark | Clock | Meaning |
|---|---|---|
| **process-start** | `Process.GetCurrentProcess().StartTime` | OS process creation (includes runtime init, same anchor as FluentApp `[boot] runcore-entry: sinceProcessStart`). |
| **first-present** | `D3D12Device.FirstPresentQpc` | First **successful** `IDXGISwapChain::Present` on the render thread. Fallback if that stamp is still 0: first pumped frame with `AppHost.LastStats.Presented`. |
| **session-restored** | first frame after `WaveeShell.ProbeNav != null` | `WaveeShell.RestoreSessionNav` runs at shell init on the same mount that wires `ProbeNav`. The probe does **not** timestamp the restore call itself (`WaveeShell` is not this probe's file). Treat this as “shell mounted and session-nav apply has been attempted,” not “every restored route’s data is on screen.” |

`DiagnosticRun` fires after `window.Show()` and **before** the first frame (`WaveeNavProbe` comments the same race). The probe pumps frames (latency-wait + vsync suppressed) until both marks land or 1200 frames elapse.

## GPU vs working set

iGPU D3D12 resources live in shared system memory and inflate `WorkingSet64`. The render thread snapshots `IDXGIAdapter3::QueryVideoMemoryInfo` into `GpuVideoMemorySnapshot` (`D3D12Device.LastVideoMemory`). About and this probe read that cold struct only.

- **LOCAL** = adapter-local (VRAM on discrete, system RAM on UMA/iGPU).
- **NON_LOCAL** = the other segment (system-memory overlap on discrete; usually empty on UMA).

Derived “app memory excl. GPU assets” ≈ working set − the shared-segment usage (LOCAL on iGPU/UMA, NON_LOCAL on discrete). Settings → About shows the same split.

## Chain

Installed in `Program.cs` `FluentApp.DiagnosticRun`, **in front of** `WaveePerfBench.TryRun`:

`WaveeStartupBench.TryRun || WaveePerfBench.TryRun || WaveeNavProbe.TryRun || …`

Returning `true` skips the interactive loop (FluentApp diagnostic-harness contract).

## Source

- Probe: `src/apps/Wavee/Features/Diagnostics/WaveeStartupBench.cs`
- About receipts: `src/apps/Wavee/Features/Shell/SettingsPage.About.cs` (`WaveeNowReceipts`, 5s `UseInterval`)
- Snapshot: `GpuVideoMemorySnapshot` in `src/FluentGpu.Windows/D3D12/D3D12MemoryDiagnostics.cs`, published from `D3D12Device.Present`
