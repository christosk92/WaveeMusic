# On-device AI lyrics sync — implementation plan (revision 2)

Written after reading both repos' CLAUDE/AGENTS, the whole `src/apps/Wavee/AiLyrics/*` tree, the engine's `FluentGpu.WindowsApi` pillars (`Power/PowerSession.cs`, `Network/NetworkStatus.cs`, `Media/SystemMediaControls.cs`, `Shell/TaskbarManager.cs`, `Storage/AppDataStore.cs`), its tests/smoke harness, the lyrics store/view/authority code, Settings/Setup UI precedents, the lab results and the C# run log. Every type/signature below was checked against the tree unless marked "new".

Paths: APP = `C:\wavee\fgpu\.claude\worktrees\lyrics-align\WaveeMusic`, ENG = `C:\wavee\fgpu\.claude\worktrees\lyrics-align\fluent-gpu`, LAB = `C:\wavee\fgpu\.claude\worktrees\lyrics-align\lab`.

---

## 0. Facts that shape the plan (found while reading)

1. **TerraFX already projects DXCore.** `TerraFX.Interop.Windows 10.0.26100.6` (the version `FluentGpu.WindowsApi.csproj` pins) has `DirectX.DXCoreCreateAdapterFactory(Guid*, void**)`, `IDXCoreAdapterFactory`, `IDXCoreAdapterFactory1.CreateAdapterListByWorkload(DXCoreWorkload, DXCoreRuntimeFilterFlags, DXCoreHardwareTypeFilterFlags, Guid*, void**)`, `IDXCoreAdapterList.GetAdapterCount/GetAdapter`, `IDXCoreAdapter.GetProperty/GetPropertySize/IsPropertySupported`, `DXCoreAdapterProperty.{HardwareID,HardwareIDParts,DriverDescription,DriverVersion,IsHardware,IsIntegrated,DedicatedAdapterMemory}`, `DXCoreHardwareID{vendorID,deviceID,subSysID,revision}`, `DXCoreHardwareTypeFilterFlags.NPU`, `DXCoreWorkload.MachineLearning`. It does **not** project the attribute GUIDs (`DXCORE_HARDWARE_TYPE_ATTRIBUTE_NPU`); those are restated locally exactly as `PowerSession.cs` restates `ES_*`/`PBT_*`. So the engine pillar is house-style vtable calls, not raw `(*(void***)p)[slot]` arithmetic as in the current `AiLyrics.Capability.cs`.
2. **`Lyrics.RowShape.SameRow` (APP `src/apps/Wavee/Shell/Lyrics.cs:251`) compares `IsWordByWord` and the syllables.** Every progressive AI publish that upgrades a few lines therefore makes `PrepareDocument` (`Lyrics.UI.cs:722`) bump `_docEpoch` and remount **every** row (`RowKey = "ll"+_docEpoch+":"+index`, `Lyrics.UI.cs:310`). With a publish every ~8 s that is a whole-list remount per publish. The plan changes keying to per-row epochs (§3.4) so only the rows that gained timing remount, off the active line.
3. **`Lyrics.Authority.IsRicher` (`Lyrics.cs:1078`) refuses an equal-rank, equal-syllable-count document.** The job's final publish (after the last snap/fill) can have the same syllable count as the last progressive one and would be refused; and a human word-by-word provider arriving later would compete on syllable count with the generated doc. §3.2 adds two explicit tie-break rules.
4. **`Lyrics.Store.Commit` is private and `Store.Ensure` is only called by lyrics surfaces** (`Lyrics.UI.cs:655`, `:2270`, `Stage.UI.cs:623`). The AI host never needs to fetch lyrics itself: it reacts to `Store.Changed` and runs when a surface asked (privacy-neutral: no extra provider traffic). Prefetch-for-every-track is a follow-up toggle.
5. **The UI poster** is one `Action<Action>` installed in `Shell.Host.InstallMarshallers()` (`Shell.Host.cs:355-376`) into `Playback.ToUi`, `Spotify.Post`, `Store.Post`, `Palette.Post`, `Lyrics.Store.ToUi`. The AI facade takes the same `post`.
6. **Power/metered already come from the engine**: `PowerSession.ReadPower().EnergySaverOn` is polled every 2 s on the UI thread in `Platform.ReadPlugged()` (`Platform.Host.cs:360`) and `Platform.Network.Cost/Metered` signals (`Platform.Settings.cs:153`) are NLM-fed. Only a `Signal<bool>` for energy saver is missing app-side; no new Windows API is needed for either.
7. **Measured C# pipeline (LAB `cs-run.log`)**: HUMBLE 177 s → 54.8 s (separate 45.2, align 7.5); first-run compile 11 graphs 101 s (separator ~25 s, layers_a/b ~10-12 s each, convs ~10 s). Python separation ≈ NPU-bound (~7.5-9 s/song). Target in §8 step A4: separation ≤ 1.5× NPU time.
8. `AiLyrics.Pack.cs` references `Rules.FormatBytes` and `Rules.SpeedMeter` which do not exist yet (it has never compiled). `Settings.StorageFormat.Bytes(long)` already formats bytes; reuse it.
9. Glyphs that exist in `ENG/src/FluentGpu.Controls/glyphs.json` for the new catalog rows: `RefineSparkle`, `Font`, `Document`, `LocaleLanguage`, `Folder`, `FolderOpen`, `Download`, `Delete`, `Clock`, `Globe`, `Repair`. Not present: Battery, PowerButton, Translate, Sparkle, AlignLeft.
10. GUI profile isolation exists: `Platform.ProfileRoot = Diagnostics.Probe.ProfileArg(args)` (`App.cs:84`); manual checks run with a scratch profile so the owner's instance is never touched.

---

## 1. Architecture and ownership

### 1.1 Engine (`FluentGpu.WindowsApi`): new pillar `Devices/`

Charter check (`ENG/CLAUDE.md`): WindowsApi = "OS-services pillars … AOT-clean Win32/WinRT interop (no WindowsAppSDK NuGet, no CsWinRT)". DXCore adapter enumeration is a Windows OS service → it belongs here. The current app file `APP/src/apps/Wavee/AiLyrics/AiLyrics.Capability.cs` is **deleted** and replaced by:

```
ENG/src/FluentGpu.WindowsApi/Devices/ComputeAdapterInfo.cs    (namespace FluentGpu.WindowsApi.Devices) — pure types + pure helpers
ENG/src/FluentGpu.WindowsApi/Devices/ComputeAdapters.cs       — the DXCore call-out (TerraFX vtables)
```

Public surface (new):

```csharp
namespace FluentGpu.WindowsApi.Devices;

/// <summary>The DXCore hardware-type filter, one value per DXCoreHardwareTypeFilterFlags bit.</summary>
public enum ComputeAdapterKind : byte { Gpu, ComputeAccelerator, Npu, MediaAccelerator }

/// <summary>PCI vendor ids DXCore reports (the NPU vendors Windows ML knows + the GPU trio).</summary>
public enum AdapterVendor : byte { Unknown, Qualcomm /*0x4D4F4351 "QCOM"*/, Intel /*0x8086*/, Amd /*0x1002*/, Nvidia /*0x10DE*/, Microsoft /*0x1414*/ }

public readonly record struct ComputeAdapterInfo(
    ComputeAdapterKind Kind, uint VendorId, uint DeviceId, uint SubSysId, uint Revision,
    string Description, ulong DriverVersionRaw, bool IsHardware, bool IsIntegrated, ulong DedicatedMemoryBytes)
{
    public AdapterVendor Vendor => ComputeAdapterVendors.FromPciId(VendorId);
    public string DriverVersion => ComputeAdapterVendors.FormatDriverVersion(DriverVersionRaw);   // "31.0.210.5"
}

public static class ComputeAdapterVendors   // pure, tested
{
    public static AdapterVendor FromPciId(uint vendorId);
    public static string FormatDriverVersion(ulong packed);        // 4×16-bit fields, high to low
    public static string DisplayName(AdapterVendor v);             // "Qualcomm", …
}

[SupportedOSPlatform("windows10.0.19041")]   // dxcore.dll shipped in 2004; Factory1/workload filter in 24H2 (26100)
public static unsafe class ComputeAdapters
{
    /// <summary>True when dxcore.dll loads and DXCoreCreateAdapterFactory succeeds (any Win10 2004+ box).</summary>
    public static bool IsSupported { get; }
    /// <summary>Hardware adapters of one kind. Empty on any failure — never throws (fail-soft like NetworkStatus).
    /// Safe on any thread; takes ~1-5 ms; NOT a per-frame call.</summary>
    public static IReadOnlyList<ComputeAdapterInfo> Enumerate(ComputeAdapterKind kind);
}
```

Implementation notes for `ComputeAdapters.Enumerate`:
- `NativeLibrary.TryLoad("dxcore.dll", …, DllImportSearchPath.System32)` is not needed: TerraFX declares `DXCoreCreateAdapterFactory` as a `[DllImport("dxcore")]`; wrap the call in `try/catch (DllNotFoundException)` → empty list (the `IsSupported` probe does the same once and caches).
- `Guid iid = __uuidof<IDXCoreAdapterFactory>(); DXCoreCreateAdapterFactory(&iid, (void**)&factory)`.
- Prefer `IDXCoreAdapterFactory1` (QI; present on 24H2): `CreateAdapterListByWorkload(DXCoreWorkload.MachineLearning /*for Npu; Graphics for Gpu*/, DXCoreRuntimeFilterFlags.None, flagsFor(kind), &listIid, &list)`. Fallback when the QI fails (pre-24H2): `IDXCoreAdapterFactory.CreateAdapterList(1, &attr, &listIid, &list)` with the locally restated GUIDs `DXCORE_HARDWARE_TYPE_ATTRIBUTE_NPU {D46140C4-ADD7-451B-9E56-06FE8C3B58ED}`, `DXCORE_ADAPTER_ATTRIBUTE_D3D12_GRAPHICS {0C9ECE4D-2F6E-4F01-8C96-E89E331B47B1}`, `DXCORE_ADAPTER_ATTRIBUTE_D3D12_CORE_COMPUTE {248E2800-A793-4724-ABAA-23A6DE1BE090}` (restated from `dxcore_interface.h`, SDK 10.0.26100 — comment the source like `PowerSession.cs` does).
- Per adapter: `IsPropertySupported`+`GetProperty(DXCoreAdapterProperty.IsHardware, 1, &b)`; `GetProperty(HardwareID, sizeof(DXCoreHardwareID), &hw)`; `GetProperty(DriverVersion, 8, &ulong)`; `GetPropertySize(DriverDescription)` then `GetProperty` into a stackalloc/pooled byte buffer (ANSI, `Marshal.PtrToStringAnsi`); `DedicatedAdapterMemory`, `IsIntegrated` when supported. Release everything in `finally`. Software adapters are skipped.
- Thread/COM: DXCore needs no `CoInitialize`; document "any thread".

Engine docs/metadata to touch: `ENG/src/FluentGpu.WindowsApi/WindowsApiInfo.cs` (add the pillar to the list), the csproj header comment, `ENG/CLAUDE.md` src-layout line (add `Devices/`), `ENG/docs/guide/windows-integration.md` (new table row "Compute devices (NPU) | Which NPU this PC has, for on-device AI features | `Devices/ComputeAdapters` | …").

Engine tests (`ENG/src/FluentGpu.Windows.Tests/ComputeAdapterInfoTests.cs`, new):
- `FromPciId` maps 0x4D4F4351→Qualcomm, 0x8086→Intel, 0x1002→Amd, 0x10DE→Nvidia, 0→Unknown.
- `FormatDriverVersion(0x001F_0000_00D2_0005)` == "31.0.210.5"; 0 → "0.0.0.0".
- Live, fail-soft fact: `ComputeAdapters.Enumerate(ComputeAdapterKind.Gpu)` does not throw; every returned entry has `IsHardware == true` and a non-empty `Description` (the box always has a GPU; `Npu` may be empty). Same style as `SingleInstanceGateTests` doing real Win32 round-trips.

Engine smoke (`ENG/src/FluentGpu.WindowsApp/Probes/WindowsApiSmoke.cs`): add `DevicesSuite()` ("[10] Devices — ComputeAdapters (DXCore)"): `IsSupported` returns; `Enumerate(Gpu)` ≥ 1 on a desktop; `Enumerate(Npu)` prints count + vendor/driver (`[PASS]` either way); update the "all nine pillars" sentence.

Engine gates: `dotnet build src/FluentGpu.slnx` Debug + Release, `dotnet test src/FluentGpu.Windows.Tests` Debug + Release, `dotnet run --project src/FluentGpu.VerticalSlice` (unaffected transitive closure; VerticalSlice does not reference WindowsApi, still must pass), `dotnet run --project src/FluentGpu.WindowsApp -- --windowsapi-smoke`.

Power/battery saver and metered: **no new engine API**. The app consumes the existing `PowerSession.ReadPower()` poll and `NetworkStatus` subscriptions. Optional follow-up (not in this change): `PowerSession.SubscribeEnergySaver(Action<bool>)` via `PowerSettingRegisterNotification(GUID_POWER_SAVING_STATUS)` for a push instead of the 2 s poll.

Disk free space (`DriveInfo.AvailableFreeSpace`), process architecture (`RuntimeInformation.ProcessArchitecture`) and OS build (`Environment.OSVersion.Version.Build`) are BCL, not Windows API interop → stay app-side in `AiLyrics.Rules`/`AiLyrics.Host`.

### 1.2 ONNX Runtime binding stays in the app (`AiLyrics.Ort.cs`)

Judgement against the engine charter:
- It is not an OS service: onnxruntime.dll + the QNN plugin are third-party binaries the **app** downloads at runtime from PyPI; nothing in Windows provides them. `WindowsApi` is "WinAppSDK-shaped OS services" — a vendored-runtime C-API binding would be the only non-OS dependency in the project and would ship in the public `FluentGpu` NuGet with no engine consumer.
- The binding is pinned to the pack (API table v30 indices, the `QNNExecutionProvider` registration name, `htp_performance_mode`, EP-context cache file naming); every one of those is a property of **this pack version**, versioned by `AiLyrics.PackVersion`, not of the engine.
- Engine gates cannot exercise it (no NPU/runtime in the engine build; VerticalSlice is headless), so it would be untested code in the engine. In the app it is covered end-to-end by `Wavee.LyricsLab --ai`.
- If a second consumer appears (e.g. a video feature or the gallery), promote it to a **separate** `FluentGpu.Onnx` project with its own charter, never into `WindowsApi`.

Keep in the app: `AiLyrics.Ort.cs`, `AiLyrics.Models.cs`, `AiLyrics.Engine.cs`, `AiLyrics.Audio.cs`, `AiLyrics.Align.cs`, `AiLyrics.Dsp.cs`, `AiLyrics.Pack.cs`, `AiLyrics.PackManifest.cs`.

### 1.3 App file map (final)

```
APP/src/apps/Wavee/AiLyrics/
  AiLyrics.cs              constants, paths, the two status signals, the public verbs (UI thread)            [extend]
  AiLyrics.Rules.cs        CORE pure decisions (availability, phases, eligibility, progress/ETA, loc-key maps)  [new]
  AiLyrics.Host.cs         HOST: Install/Shutdown, the Driver component's effects, worker thread, download task, results cache I/O [new]
  AiLyrics.Results.cs      results-cache codec (pure) + sweep policy                                           [new]
  AiLyrics.Diag.cs         per-track report ring for the inspector                                            [new]
  AiLyrics.UI.Settings.cs  the Settings card (Appearance ▸ Lyrics)                                            [new]
  AiLyrics.UI.Lyrics.cs    rail-header sparkle button + end-of-lyrics strip + inspector rows                  [new]
  AiLyrics.Pack.cs         (exists) compile fixes, Pause, per-file label, no metered check inside              [edit]
  AiLyrics.Ort.cs / Models.cs / Engine.cs / Audio.cs / Align.cs                                                [small edits]
  AiLyrics.Dsp.cs          performance rewrite (§8 A4)                                                        [rewrite]
  AiLyrics.PackManifest.cs generated from ops/ai/lyrics-pack.v1.json                                           [regenerate]
  AiLyrics.Capability.cs   DELETE (engine pillar)
APP/src/apps/Wavee/Platform/Platform.cs           Keys                                                         [edit]
APP/src/apps/Wavee/Platform/Prefs.cs              Prefs.AiLyrics epoch + readers                               [edit]
APP/src/apps/Wavee/Platform/Platform.Host.cs      Platform.Power.EnergySaver signal                            [edit]
APP/src/apps/Wavee/Shell/Lyrics.Host.cs           Store.Upgrade seam                                           [edit]
APP/src/apps/Wavee/Shell/Lyrics.cs                Authority tie-breaks; RowShape.ChangedRows                   [edit]
APP/src/apps/Wavee/Shell/Lyrics.UI.cs             per-row keys; mount the AI strip; debug-overlay row          [edit]
APP/src/apps/Wavee/Shell/Rail.UI.cs               mount the sparkle header button                              [edit]
APP/src/apps/Wavee/Shell/Shell.Host.cs            AiLyrics.Install(post) / AiLyrics.Shutdown()                 [edit]
APP/src/apps/Wavee/Shell/Shell.UI.cs (root)       mount AiLyrics.Driver() beside the ambient-power tick        [edit]
APP/src/apps/Wavee/Screens/Settings.cs            Catalog rows                                                  [edit]
APP/src/apps/Wavee/Screens/Settings.UI.Appearance.cs  mount the card under the Lyrics section                  [edit]
APP/src/apps/Wavee/Screens/Settings.UI.Privacy.cs  "What leaves this PC" row                                   [edit]
APP/src/apps/Wavee/Screens/Diagnostics.UI.cs      LyricsInspectorBody: AI card                                  [edit]
APP/src/apps/Wavee/assets/loc/en-US.json          settings.lyrics.ai.*, lyrics.ai.*                             [edit]
APP/src/apps/Wavee.Tests/AiLyrics*Tests.cs, LyricsAuthorityTests additions, RowShapeTests                       [new/edit]
APP/src/apps/Wavee.LyricsLab/Program.cs           --ai timing table, --bench-dsp, --check-results               [edit; commit the tool, keep out of Wavee.slnx]
APP/ops/ai/lyrics-pack.v1.json, New-AiLyricsPackManifest.ps1, Test-AiLyricsPack.ps1, tests/*.Tests.ps1       [new]
APP/docs/guide/ai-lyrics-pack.md                  hosting runbook + smoke check                                 [new]
APP/CHANGELOG.md, PRIVACY.md, ops/build/notices-extra.json, docs/guide/microsoft-store-onboarding.md           [edit]
APP/docs/plans/wavee/ai-lyrics-sync-implementation.md  replace with this plan's content                        [rewrite]
```

---

## 2. State machine, status model, threading

### 2.1 Types (pure, in `AiLyrics.Rules.cs` unless noted)

```csharp
public enum Availability : byte { Available, NotArm64, OsTooOld, NoNpu, NpuNotSupported }

/// <summary>The setup lifecycle the Settings card renders. One value, written only on the UI thread.</summary>
public enum SetupPhase : byte { Unavailable, Off, NeedsSetup, Downloading, Paused, Preparing, Ready, Error }

public enum SetupError : byte { None, Offline, Network, NotFound, HashMismatch, DiskFull, RuntimeLoad, NoNpuDevice, Compile, Cancelled, Unknown }  // exists in Pack.cs; move here

public readonly record struct DownloadProgress(DownloadPhase Phase, long Done, long Total, double BytesPerSecond, string Label); // exists
public readonly record struct PrepareProgress(int Done, int Total, long WeightDone, long WeightTotal, bool Recompile, bool Finishing);

/// <summary>Everything the card and the header need, published WHOLE (torn reads impossible). UI thread only.</summary>
public readonly record struct Status(
    SetupPhase Phase, Availability Availability, bool Enabled,
    string NpuName, string NpuDriver,                       // "Qualcomm Hexagon NPU", "31.0.210.5"
    DownloadProgress Download, PrepareProgress Prepare,
    SetupError Error, string ErrorDetail,
    long InstalledBytes, IReadOnlyList<string> Languages,   // installed aligner languages
    long PendingDownloadBytes);                             // what NeedsSetup would fetch

public enum TrackPhase : byte { Idle, Waiting, Working, Done, Skipped }
public enum SkipReason : byte { None, NoLyrics, AlreadyWordByWord, LanguageNotInstalled, PlainTextOff, WordSyncOff, Podcast, NotSpotifyAudio, TooLong, BatterySaver, AudioUnavailable, Failed, NeedsSetup }

public readonly record struct TrackStatus(string TrackId, TrackPhase Phase, SkipReason Reason, string Language,
    int LinesReady, int LineCount, double ProcessedSeconds, bool FromCache);
```

Signals (in `AiLyrics.cs`, written **only** on the UI thread through `post`):
```csharp
public static readonly Signal<Status> Current = new(Status.Unknown);
public static readonly Signal<TrackStatus> Track = new(default);
```

Pure transitions (`Rules`), each unit-tested:
- `Availability Rules.Availability(bool arm64, int osBuild, IReadOnlyList<ComputeAdapterInfo> npus)` → NotArm64 / OsTooOld (< 26100) / NoNpu / NpuNotSupported (no Qualcomm) / Available.
- `SetupPhase Rules.InitialPhase(Availability, bool enabled, bool installedComplete)`.
- `bool Rules.CanToggleOn(Availability)`; `bool Rules.NeedsMeteredConfirm(bool metered, long bytes)` (metered && bytes > 50 MB).
- `SkipReason Rules.Eligibility(Lyrics.Doc? doc, EntityKind kind, bool spotifyUri, long durationMs, bool wordSync, bool plainText, IReadOnlyList<string> languages, bool energySaver, bool keepOnSaver, SetupPhase phase)`: order NeedsSetup → Podcast → NotSpotifyAudio → NoLyrics → AlreadyWordByWord (any `IsWordByWord` line with syllables) → `Unsynced ? (plainText ? ok : PlainTextOff) : (wordSync ? ok : WordSyncOff)` → LanguageNotInstalled (`doc.Language ?? "en"`) → TooLong (> 20 min) → BatterySaver.
- `string Rules.LanguageOf(Doc)`; `string Rules.ResultsKey(trackId, packVersion)`; `ulong Rules.SourceHash(IReadOnlyList<Line>)` (FNV-1a over line texts — protects a cached result against a changed provider doc).
- `SpeedMeter` (EWMA 3 s; `bool Sample(long done, long nowMs)` true at most every 100 ms), `double? Rules.EtaSeconds(done,total,bps)`, `string Rules.EtaKey(double? s, out int n)` → `lyrics.ai.eta.{seconds,minutes,hours,unknown}`.
- `long[] Rules.PrepareWeights(IReadOnlyList<string> graphs, Func<string,long> bytesOf)` = max(bytes, 16 MiB) each (separator and the two transformer halves dominate compile time; conv0 at 17 KB still costs ~3 s, hence the floor); `double Rules.PrepareFraction(weightDone, weightTotal, done, total)`.
- `string Rules.ErrorKey(SetupError)` → the `settings.lyrics.ai.error.*` keys; `bool Rules.ErrorOffersRetry(SetupError)` (all but Cancelled), `bool Rules.ErrorOffersRemove(SetupError)` (RuntimeLoad, Compile).
- `HeaderState Rules.Header(Status, TrackStatus, EntityKind)` → `(bool Visible, bool Active, string TipKey, bool OpensSettings)`.
- `bool Rules.ShowsFooter(TrackStatus)` (Working or Done), `string Rules.FooterKey(TrackStatus)`.
- `bool Rules.UnloadAfterIdle(long lastJobEndMs, long nowMs)` (10 min) — frees the NPU contexts when lyrics are not in use; reload costs ~1.5 s.
- `bool Rules.ToastOnReady(bool settingsPageVisible)` / `ToastOnError(…)` (toast only when the card is not on screen).

### 2.2 Threads

```
UI thread            AiLyrics.Driver (a Component in the shell root) — UseSignalEffects on Playback.CurrentId,
                     Playback.PositionMs/IsPlaying, Lyrics.Store.Changed, Platform.Power.EnergySaver, Prefs.AiLyrics.Epoch,
                     Platform.Network.Metered. Writes Current/Track. Posts commands to the worker. Never waits.
download Task        Task.Run(Pack.InstallAsync) with its own CTS; progress → post(() => Current.Value = …) ≤ 10 Hz.
"wavee-ai-lyrics"    one Thread (IsBackground, BelowNormal): BlockingCollection<Command>; owns LoadedModels; runs one
                     TrackJob at a time; publishes through Lyrics.Store.Upgrade (which hops via Store.ToUi).
"wavee-ai-dsp"       owned by Separator after A4: computes STFT of window k+1 / iSTFT of window k-1 while the NPU runs k.
boot Task            Task.Run: ComputeAdapters.Enumerate(Npu), Pack.Installed() scan, Pack.FinishPendingRemoval() → post(Seed).
```

Commands: `enum Cmd { Load, Unload, Start, Cancel, Shutdown }`, `sealed record JobRequest(string TrackId, string Uri, long DurationMs, Lyrics.Doc Source, string Language, CancellationTokenSource Cts)`. The UI thread cancels the running job's CTS **before** enqueuing `Start`/`Cancel` (the job checks the token between NPU calls, ≤ ~250 ms), so the worker never blocks on a command it cannot reach.

Playhead for JIT commits: the Driver writes `static int s_posMs, s_playing; static long s_posAtTick` (volatile) from `Playback.PositionMs`/`IsPlaying`; the worker's `Func<double>` extrapolates `(s_posMs + (playing ? TickCount64 - s_posAtTick : 0)) / 1000`.

Event handling (all decided by `Rules`, executed by the Driver):
- **Track change** (`Playback.CurrentId`): cancel current job; `Track.Value = Waiting`; if `Store.Answered(id)` evaluate now, else wait for `Store.Changed`.
- **Store.Changed**: `doc = Store.Doc(currentId)`; if `doc.Generated` → nothing (our own publish); else if a result exists in the results cache (and `SourceHash` matches) → `Store.Upgrade(overlay)` immediately, `Track = Done(FromCache)`; else `Eligibility` → `Start` or `Skipped(reason)`. Also covers the late-provider case: a provider promotion that overwrote our doc in `Store.Docs` is re-upgraded from the last published AI doc kept per track (`_lastPublished`).
- **Seek**: nothing (the job's deadline follows the playhead; committed words stay; forward seeks are caught up at 3-10× real time).
- **Pause**: nothing (processing continues to EOF; commits stall at the horizon until play resumes).
- **Battery saver flips on** and `keepOnSaver == false`: cancel job, `Track = Skipped(BatterySaver)`, `Unload` models after the idle timeout; flips off → re-evaluate the current track (the job restarts from the song's start; words before the playhead commit immediately).
- **Prefs epoch** (master off): cancel + `Unload`; `Current.Phase = Off`. Master on with files present: `Load` (warm start) and re-evaluate.
- **Metered** only matters at download start (confirm dialog) and while downloading (no auto-pause; the user chose).
- **App shutdown** (`Shell.Host.Run` finally, after `Playback.Os.Shutdown()`): `AiLyrics.Shutdown()` cancels the download CTS (partials stay; resumable) and the job, enqueues `Shutdown`, `Join(2000)`, disposes models. Logs if the join timed out.
- **Idle unload**: a `UseInterval` in the Driver (60 s) calls `Rules.UnloadAfterIdle`.

Preparing (first NPU compile) runs on the worker inside `LoadedModels.Load` with the existing `preparing(done,total,cached)` callback extended to `(graphIndex, graphPath, cached, phase)`; posts `PrepareProgress`. Cancel is honoured between graphs; after Cancel the card shows "Finishing this step…" (`Finishing = true`) until the current graph returns. A stale-cache retry (`LoadedModels.Open`) sets `Recompile = true` so the card says why.

### 2.3 UI thread guarantees
- Nothing on the UI thread touches the disk except `Platform.Settings` (registry) — `Pack.Installed()`, `Pack.DiskUsed()`, `Directory` enumeration, DXCore enumeration and `DriveInfo` all run in `Task.Run` and post.
- Progress writes are throttled at the source (≤ 10 Hz downloads; per graph for prepare; per publish for tracks).
- The Settings card is its own `Component` bound to `AiLyrics.Current`; it never calls `Settings.Bump()` for progress.

---

## 3. Lyrics integration

### 3.1 The seam — `Lyrics.Store.Upgrade(Doc)` (APP `Shell/Lyrics.Host.cs`, beside `Refetch`)

```csharp
/// <summary>A DERIVED document for a track this store already answered (the on-device aligner). Validated — Generated,
/// non-empty, a known id — then committed on the UI thread and announced through Upgraded exactly like an aggregator
/// promotion. Never touches the aggregator, its memory cache or its disk cache: the derived document is a layer over the
/// provider's, re-applied from the aligner's own results cache on the next play.</summary>
public static void Upgrade(Doc doc)
{
    if (doc is not { Generated: true, Lines.Count: > 0 } || string.IsNullOrEmpty(doc.TrackId)) return;
    if (!Answered(doc.TrackId)) return;
    Commit(doc.TrackId, doc, upgrade: true);
}
```
`Commit` already hops via `ToUi` and bumps `Changed`/`Upgraded`; the view's existing `SyncFromStore → ReceiveUpgrade → Authority.IsRicher → ApplyImmediately-or-hold` path applies it at the next line handoff (`Lyrics.UI.cs:679-718`). `Store.Clear()` (logout) drops it like everything else.

### 3.2 Ranking rules — `Lyrics.Authority.IsRicher` (APP `Shell/Lyrics.cs:1078`)

```csharp
public static bool IsRicher(Doc next, Doc current)
{
    int nr = Richness(next), cr = Richness(current);
    if (nr != cr) return nr > cr;
    if (nr < 3) return false;
    // Human word timing outranks generated word timing, whatever the counts; generated never displaces human.
    if (current.Generated && !next.Generated) return true;
    if (!current.Generated && next.Generated) return false;
    int ns = SyllableCount(next), cs = SyllableCount(current);
    // A later revision of the SAME generated document (the aligner publishes progressively, then once more after its
    // final snap/fill pass) replaces the earlier one even when no new word was timed.
    if (next.Generated && current.Generated && string.Equals(next.Provider, current.Provider, StringComparison.Ordinal))
        return ns >= cs;
    return ns > cs;
}
```
Tests in `LyricsAuthorityTests` (extend the existing lyrics rules tests): generated-vs-line wins; human-syllable-vs-generated wins; generated-vs-human refused; same-provider generated revision with equal count accepted; different-provider generated equal count refused.

Disk-cache interplay: `Aggregator.SaveToDiskIfBetter` is called only from aggregator passes (`Lyrics.Host.cs:1506`, `:1653`); `Store.Upgrade` bypasses the aggregator, so `%LOCALAPPDATA%\Wavee\lyrics` never holds a generated doc and can never "resurrect" one. What it can do is resurrect the **provider** doc on the next play — by design; the AI results cache (§3.3) re-layers. The aggregator's memory cache (`Peek`) likewise returns the provider doc; the Driver's `Store.Changed` handler re-applies (`_lastPublished[trackId]` in-session, results file across sessions).

### 3.3 Results cache (`AiLyrics.Results.cs`)

File `ai\lyrics\results\<trackId>.v<PackVersion>.json`:
```json
{ "v": 1, "trackId": "…", "pack": 1, "language": "en", "origin": "spotify", "sourceHash": "a1b2…",
  "savedUnixMs": 0, "lines": [ { "s": 12340, "e": 15000, "w": [[12340,12600,"Nobody "],[12600,13100,"pray "]] } ] }
```
- `Results.Write(Doc final)` on the worker at job end; `Results.TryRead(trackId, packVersion, out ResultDoc)`; `Doc Results.Overlay(ResultDoc r, Doc provider)` rebuilds `Lyrics.Line`s with the **provider's** text/translation/romanization and the cached syllable timing (syllable texts are re-cut from the provider text at the stored word boundaries via `Align.Words`), yielding a `Generated` doc. A `sourceHash` mismatch or a line-count mismatch → miss (re-run).
- Sweep: keep ≤ 500 files / 20 MB, oldest first, run once at boot on the boot Task. `Pack.Remove()` deletes the folder too.
- Codec is pure (string in/out) → `AiLyricsResultsTests`.

### 3.4 Smooth progressive upgrades — per-row keys (APP `Shell/Lyrics.cs` + `Lyrics.UI.cs`)

Add to `Lyrics.RowShape`:
```csharp
/// <summary>Per row: does the mounted row for current[i] survive next[i]? Same count required (caller checks).</summary>
public static void ChangedRows(Doc current, Doc next, Span<bool> changed)   // changed[i] = !SameRow(a[i], b[i])
```
In `LyricsView` (`Lyrics.UI.cs`): keep `_docEpoch` for a count change or a different track; add `int[] _rowEpoch` and make `RowKey(i) => "ll" + _docEpoch + ":" + _rowEpoch[i] + ":" + i`. In `PrepareDocument`: `sameCount = previous is not null && previous.Lines.Count == doc.Lines.Count && same track` → keep every per-line array (`_lineNodes`, `_glowAlpha`, `_lineEmphasis`, `_dofCurrent`, cascade arrays) and for each `changed[i]`: `_rowEpoch[i]++`, `_lineRunLen[i] = NaN`, `_lineNodes[i] = default` (the remounted row reports a fresh handle through `ReportLineNode`). Only then does a progressive publish remount the handful of upcoming rows that gained word timing, never the active line (the JIT commit is ≥ 8 s ahead and the hold rule applies at a handoff). `RowShapeTests` pins `ChangedRows`. Verify in the running app that a publish causes no scroll jump (the follow geometry keys off `_lineNodes` of untouched rows).

### 3.5 Provider/metadata fields
`Lyrics.Doc` already has `Language/Origin/Generated` and `SpotifyNative` parses `lyrics.language` (done). The AI doc: `Provider = "wavee-ai"`, `Origin = source.Provider`, `Language`, `Generated = true`, `OffsetMsApplied = 0`, `Sync = Syllable`, lines `IsWordByWord = true` once committed (exists in `TrackJob.BuildDoc`).

### 3.6 Inspector diagnostics
`AiLyrics.Diag` (new): `public sealed record TrackReport(string TrackId, TrackPhase Phase, SkipReason Reason, string Language, string Npu, int LinesReady, int LineCount, double SeparateS, double AlignS, double TotalS, double FirstPublishS, bool FromCache, long WhenUnixMs, string Detail)`; ring of 24 + by-track map (same shape as `Lyrics.Diag`); `Publish` from the worker, `ForTrack(id)` from the UI. Render:
- `Screens/Diagnostics.UI.cs` `LyricsInspectorBody` providers tab: one extra card "wavee-ai" after the sources (phase dot colours reuse `Good/Warn/Bad/Dim`), lines: state + reason, model/NPU/driver, lines ready/total, timings, cache hit.
- `Shell/Lyrics.UI.cs` debug overlay (`Overlay(trackId)`, line 2132): one `SourceRow`-styled row for the same report.

---

## 4. Settings and UX

### 4.1 Keys (`Platform/Platform.cs` → `Platform.Keys`)
```csharp
public static readonly SettingKey<bool>   AiLyricsEnabled        = new("lyrics.ai.enabled", false);
public static readonly SettingKey<bool>   AiLyricsWordSync       = new("lyrics.ai.wordSync", true);
public static readonly SettingKey<bool>   AiLyricsPlainText      = new("lyrics.ai.plainText", true);
public static readonly SettingKey<bool>   AiLyricsOnBatterySaver = new("lyrics.ai.onBatterySaver", false);
public static readonly SettingKey<string> AiLyricsLanguages      = new("lyrics.ai.languages", "en");   // comma-separated, managed by the card
```
`Prefs.AiLyrics` (`Platform/Prefs.cs`, same shape as `Prefs.Lyrics`): `Epoch`, `Bump()`, `Enabled()`, `WordSync()`, `PlainText()`, `OnBatterySaver()`, `Languages()` (reactive reads `_ = Epoch.Value`), `Set(key, value)` → store + bump. Gating: the master switch is enabled only when `Availability == Available`; the three sub-toggles and languages are items of the Ready expander; nothing downloads until the user confirms the size.

`Platform.Power` (new nested class in `Platform.Host.cs`): `public static readonly Signal<bool> EnergySaver = new(false)` written in `ReadPlugged()` (UI thread tick) with `SetIfChanged` semantics.

### 4.2 Catalog rows (`Screens/Settings.cs` `Catalog.Rows`, section `Lyrics` whose glyph is `Microphone`; `lyricsBlur` holds `Filter`)
```
new(Tab.Appearance, "Lyrics", "aiLyrics",        "RefineSparkle"),
new(Tab.Appearance, "Lyrics", "aiWordSync",      "Font"),
new(Tab.Appearance, "Lyrics", "aiPlainText",     "Document"),
new(Tab.Appearance, "Lyrics", "aiBatterySaver",  "Clock"),
new(Tab.Appearance, "Lyrics", "aiLanguages",     "LocaleLanguage"),
new(Tab.Appearance, "Lyrics", "aiFiles",         "Folder"),
```
All unique within the section and distinct from `Microphone`; `SettingsCatalogTests` enforces it automatically.

### 4.3 The card (`AiLyrics.UI.Settings.cs`, mounted from `Settings.UI.Appearance.cs` right after the `lyricsBlur` row: `kids.Add(AiLyrics.SettingsCard())`)

`AiLyrics.SettingsCard() => Embed.Comp(static () => new AiCardView())`. `AiCardView.Render()` reads `AiLyrics.Current.Value`, `Prefs.AiLyrics.Epoch.Value`; switches on `Phase` and returns ONE `SettingsExpander` (Ready) or ONE `SettingsCard` (every other phase) so the section's silhouette never changes; body arms keyed `"ai:" + phase` with `Enter = new EnterExit(Opacity: 0f, Dy: 4f, Active: true)`, `Exit = new EnterExit(Opacity: 0f, Active: true)`, `Transition = MotionTok.StandardEnter`, wrapped in a `BoxEl` with `Layout = new LayoutTransition(TransitionChannels.Size, MotionTok.ControlNormal.ToDynamics(), Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height)` so height changes reflow (the `LoginStepBar`/`Setup.UI.cs:1136` precedent). Progress bars bind `FloatSignal`s (`ProgressBar.Create(s_downloadFraction, width, state, FillEase)` with the `PartFill` reflow `TemplateParts` from `Setup.UI.cs:1132`) so a 10 Hz progress write is compositor-only.

Every visible state (header icon is always `Icons.RefineSparkle` = the Windows AI sparkle):

| Phase | Header / description | Content (right) | Items / footer |
|---|---|---|---|
| Unavailable | title / `unavailable.<reason>` | `ToggleSwitch` off, `isEnabled:false` | — |
| Off | title / sub | `ToggleSwitch` off → `AiLyrics.SetEnabled(true)` | — |
| NeedsSetup | `setup.title` / `setup.sub(size)` | `Button.Accent(Download)` → `AiLyrics.StartDownload(overlay)`; the switch stays visible and on | if `Platform.Network.IsMetered`: `ConfirmThen(metered.title, metered.body(size), metered.confirm, …)` first |
| Downloading | `downloading.title(label)` / "{done} / {total} · {speed}/s · {eta}" | determinate bar (indeterminate while Checking/Verifying) | `HyperlinkButton` Pause, Cancel |
| Paused | `paused.title` / `paused.sub(done,total)` | `Button.Standard(Resume)` | Cancel (keeps partial files) |
| Preparing | `preparing.title` / `preparing.sub(done,total,eta)` (+ `preparing.again` when Recompile) | determinate bar by weight; `preparing.finishing` after Cancel | Cancel |
| Ready | `ready.title` / "{NpuName} · {driver}" | `ToggleSwitch` on | items: aiWordSync `Toggle`, aiPlainText `Toggle`, aiBatterySaver `Toggle`, aiLanguages (English ✓ / Spanish `size` [Download]/[Remove]), aiFiles ("{size} on this PC" · `Open folder` · `Remove…` with confirm) |
| Error | `InfoBar.Create(InfoBarSeverity.Error, title: error.<kind>, message: detail, isClosable:false, actionButton: Try again / Remove)` above the NeedsSetup card | | `details` expands the raw detail (HTTP status / HRESULT / file) |

Remove: `ConfirmThen(remove.confirmTitle, remove.confirmBody(size), remove.confirmAction, AiLyrics.RemoveFiles)`; the host unloads models first, deletes models/results, defers the loaded runtime DLLs (`Pack.Remove` already marks `.remove`), status → Off; if deferred show `remove.deferred` as an informational InfoBar until restart.

Languages: `AiLyrics.InstallLanguage("es")` runs the same download path for `PackManifest.Languages["es"]` (phase Downloading with label "Spanish model"), then `Prepare` for its 5 graphs; `RemoveLanguage` deletes its files + ctx and rewrites the key. English cannot be removed while it is the only language.

Open folder: `Shell`/`FilePicker` precedent — use the existing "Open folder" verb the Storage tab uses for the logs folder (grep `OpenFolder` in `Settings.UI.Storage.cs`) with `AiLyrics.Root`.

### 4.4 Lyrics rail header (`Rail.UI.cs` `LyricsHeader`, add after the globe toggle)
`kids.Add(AiLyrics.HeaderButton())` → `Embed.Comp(static () => new AiHeaderButton())` which reads `AiLyrics.Current.Value`, `AiLyrics.Track.Value`, `Playback.CurrentId.Value.Kind` and returns `Rail.HeaderButton(Icons.RefineSparkle, Loc.Get(tipKey)…, onClick, active)` or an empty `BoxEl` when `Rules.Header(...).Visible` is false (feature off/unavailable). Tooltip keys from `Rules.Header` (§5 list); `active` = Track.Phase is Done; while Working the glyph gets `Opacity` bound to a `FloatSignal` eased between 0.55 and 1.0 by `MotionTok.StandardSpring` on each publish (a quiet pulse, no spinner). Click: `Settings.Open(Settings.Tab.Appearance)` (deep-links exist only per tab; a section anchor is optional follow-up). Same component mounted in the Stage's top bar if it has a header row (check `Stage.UI.cs` for the globe toggle mount and mirror it).

### 4.5 End-of-lyrics AI disclosure (`AiLyrics.UI.Lyrics.cs`, mounted in `Lyrics.UI.cs` lyrics view root)
Not a virtual-list item (the follow geometry assumes `ItemCount == Lines.Count`): a one-line strip **below** the list in the view's root column, composed only when `Rules.ShowsFooter(Track)` and the doc on screen is `Generated` or the job is Working: `✦ Word timing generated on this PC by AI. It can be off by a moment.` / `✦ Timing words on your NPU…` at 12/16 `InkMode.Secondary` with `Icons.RefineSparkle` 12 DIP (icon + text, never icon alone), `Enter = new EnterExit(Opacity: 0f, Active: true)`, and the root column carries the same height `LayoutTransition` reflow so the list shrinks smoothly instead of jumping when the strip appears. Also mounted in the Stage lyrics overlay.

### 4.6 Toasts (`Notify.Say`, `Platform/Controls.cs:1360`)
- Download + prepare finished: `Notify.Say(Loc.Get(Strings.Settings.Lyrics.Ai.ToastReady), InfoBarSeverity.Success, dedupeKey: "ai-lyrics-ready")` only when `Rules.ToastOnReady(settingsVisible)` (the card is not on screen).
- Setup error while Settings is closed: `Notify.Say(Strings.Settings.Lyrics.Ai.ToastFailed(reason), InfoBarSeverity.Error, actionLabel: Open settings, onAction: () => Settings.Open(Tab.Appearance), dedupeKey: "ai-lyrics-error")`.
- Per-song problems never toast (header tooltip + log + inspector).

---

## 5. Error catalogue and strings (en-US only; nl/ko-KR fall back per the loc file's contract; every key must be referenced or FLLOC005 fails)

`settings.lyrics.ai.*` (object `settings.lyrics.ai` beside the existing `settings.lyrics` group):

| Key | en-US |
|---|---|
| title | Word-by-word lyrics with on-device AI |
| sub | Times every word as it is sung. Runs on this PC's NPU; your music and lyrics stay on this PC. |
| unavailable.arm64 | Needs a Copilot+ PC with a Snapdragon processor. |
| unavailable.os | Needs Windows 11, version 24H2 or later. |
| unavailable.noNpu | This PC has no NPU. |
| unavailable.npuVendor | Your NPU isn't supported yet. Snapdragon NPUs are supported today. |
| setup.title | Download the AI models |
| setup.sub | One-time download of {size} from models.cproducts.dev and pypi.org. You can remove it later. |
| setup.action | Download |
| metered.title | You're on a metered connection |
| metered.body | This download is {size}. Download anyway? |
| metered.confirm | Download |
| downloading.title | Downloading {label} |
| downloading.metric | {done} / {total} · {speed}/s |
| eta.seconds / eta.minutes / eta.hours / eta.unknown | {n} s left / {n} min left / about {n} h left / estimating… |
| checking / verifying / installing | Checking… / Verifying… / Installing… |
| pause / resume / cancel / retry / details / remove | Pause / Resume / Cancel / Try again / Details / Remove… |
| paused.title / paused.sub | Download paused / {done} of {total} downloaded. Wavee keeps what it already has. |
| preparing.title | Optimizing for your NPU |
| preparing.sub | One time: each model is compiled for this NPU. {done} of {total} · {eta} |
| preparing.again | Your NPU driver or runtime changed, so Wavee is optimizing the models again. |
| preparing.finishing | Finishing this step… |
| ready.title | Ready |
| ready.sub | {npu} · driver {driver} |
| wordSync / wordSyncSub | Word-by-word for line-synced lyrics / Upgrade lyrics that are timed by line. |
| plainText / plainTextSub | Time lyrics that have no timing / Also align plain lyrics that arrive without timestamps. |
| batterySaver / batterySaverSub | Keep running on battery saver / Off: the AI pauses while Windows battery saver is on. |
| languages / languagesSub | Languages / One model per language, about {size} each. |
| lang.en / lang.es / lang.installed / lang.download / lang.remove | English / Spanish / Installed / Download / Remove |
| files / filesSub / openFolder | Downloaded files / {size} on this PC / Open folder |
| remove.confirmTitle | Remove the AI models? |
| remove.confirmBody | This frees {size}. Word-by-word lyrics stop until you download them again. |
| remove.confirmAction | Remove |
| remove.deferred | Some files are in use and will be removed the next time Wavee starts. |
| toast.ready | AI lyrics are ready |
| toast.failed | AI lyrics setup failed: {reason} |
| error.offline | You're offline. Connect and try again. |
| error.network | The connection dropped. Wavee kept what it already downloaded. |
| error.notFound | The AI models aren't available for download right now. |
| error.hash | A downloaded file was damaged and was removed. Try again. |
| error.diskFull | Not enough space: needs {size} free on {drive}. |
| error.runtime | The AI runtime couldn't start. |
| error.noNpuDevice | The NPU driver didn't respond. Update your NPU driver and try again. |
| error.compile | Couldn't prepare the models for your NPU. |
| error.unknown | Something went wrong. |
| error.detail | Details: {detail} |

`lyrics.ai.*` (surface): `header.on` "Words timed on this PC by AI"; `header.working` "Timing words on your NPU… {done} of {total} lines"; `header.waiting` "Waiting for lyrics…"; `header.setup` "AI lyrics need setup. Click to finish."; `header.skipped.noLyrics` "No lyrics to time"; `.alreadyWordByWord` "A lyrics provider has word timing for this song. Click to time it with AI instead." (the click sets the per-song preference, `AiLyrics.SetPreferAi`; `header.workingOverride` / `header.overriding` offer the way back); `.language` "No {language} model installed. Click to download it."; `.plainOff` "These lyrics have no timing. Turn on \"Time lyrics that have no timing\" to align them."; `.wordSyncOff` "Word-by-word upgrades are off in Settings."; `.batterySaver` "Paused while battery saver is on"; `.audio` "The song's audio isn't available for timing right now"; `.failed` "Word timing failed for this song"; `.podcast` "Not available for podcasts"; `.tooLong` "Too long to time on this PC"; `footer.generated` "Word timing generated on this PC by AI. It can be off by a moment."; `footer.progress` "Timing words · {ready} of {total} lines" above a buffer bar (timed region and playhead). Privacy: `settings.privacy.whatLeaves.ai` "AI model download", `.aiSub` "Only when you turn on word-by-word lyrics: the model files from models.cproducts.dev and pypi.org. Your music and lyrics never leave this PC."

Recovery per kind: Offline/Network/NotFound/HashMismatch/NoNpuDevice/Compile → Retry (Network resumes from the partial); DiskFull → Retry after freeing (the detail names size and drive); RuntimeLoad/Compile → also "Remove and download again"; Cancelled → back to NeedsSetup silently.

Logging: category `ai-lyrics` (always on). `Log.Event(level, "ai-lyrics", eventId, message, null, -1, null, fields)` with ids `ai.capability` (arch, build, npu count, vendor, driver), `ai.setup.phase` (from, to, reason), `ai.download.start/done/failed` (files, bytes, kind, http, file), `ai.download.progress` every 10 s (done, total, bps), `ai.prepare.graph` (graph, cached, ms), `ai.models.loaded/unloaded` (ms, graphs, ort version), `ai.job.start/publish/done/skip/failed` (trackId, language, lines, ms, reason/exception type), `ai.results.hit/miss/saved`, `ai.remove` (deferred). Never lyric text or audio.

---

## 6. Metadata updates

- `APP/CHANGELOG.md`: under the next version header (`## [0.3.1]` or whatever release prep names) `### Added` — "**Word-by-word lyrics with on-device AI.** On a Copilot+ PC with a Snapdragon NPU, Settings ▸ Appearance ▸ Lyrics can download the models (about 700 MB, one time) and Wavee then times every word of a song's lyrics on the NPU while it plays; the song and its lyrics never leave the PC. English and Spanish aligners. (#n)". The release gate requires a `(#n)` and a `Fixes #n` trailer → **ask the owner for/approve the GitHub issue before committing** (github-triage skill).
- `APP/PRIVACY.md`: "What stays on your machine" row `ai\lyrics\` (models, NPU compile caches, per-song word timings); "What leaves your machine" bullet: `models.cproducts.dev` (Cloudflare R2 bucket the author runs) and `files.pythonhosted.org` (PyPI), only when the user turns the feature on, anonymous downloads; a short "On-device AI lyrics" section: audio and lyric text are processed in-process on the NPU, nothing is uploaded, no Windows AI/Copilot services are used.
- `APP/ops/build/notices-extra.json`: ONNX Runtime 1.30.0 (MIT, microsoft/onnxruntime, "downloaded at the user's request, not bundled"); onnxruntime-qnn 2.6.0 / Qualcomm AI Engine Direct libraries (Qualcomm license — mark "VERIFY: QAIRT redistribution terms" like the OOBE entry); wav2vec2-large-960h-lv60-self (Apache-2.0, facebook); wav2vec2-large-xlsr-53-spanish (Apache-2.0, jonatasgrosman); Kim_Vocal_2 MDX-Net (MIT via Ultimate Vocal Remover — VERIFY); note they are not bundled in the MSIX. `generate-third-party-notices.ps1` picks them up.
- `APP/docs/guide/microsoft-store-onboarding.md` §3/§4: "The app offers an optional ~700 MB model download after install (user-initiated, from the author's host and PyPI); no new capability (in-process ONNX Runtime; no `systemAIModels`, no Windows AI APIs); mention it in Notes for certification."
- Settings ▸ Privacy & diagnostics ▸ "What leaves this PC": new row (`Settings.UI.Privacy.cs`, catalog row `new(Tab.PrivacyDiagnostics, "Privacy", "whatLeavesAi", "RefineSparkle")`).
- `ENG/docs/guide/windows-integration.md`, `ENG/CLAUDE.md`, `WindowsApiInfo.cs` as in §1.1.

---

## 7. Hosting runbook and smoke check

Where: **public app repo**, because nothing about the AI bucket is secret (public URLs, immutable objects, hashes already embedded in the signed binary) and the private `C:\WAVEE\wavee-dev-helpers` is scoped by its AGENTS.md to the PlayPlay runtime service (receipts, catalogs, "never move its inputs into the app repo", STATUS/OPERATIONS consistency). Do not add the AI bucket to that repo's CLI/catalog model. Optional, with the owner's approval: one sentence in its OPERATIONS.md "Out of scope: the AI model bucket `wavee-ai-models` — see WaveeMusic `docs/guide/ai-lyrics-pack.md`" so the next operator does not look for it there.

Files:
- `APP/docs/guide/ai-lyrics-pack.md`: bucket `wavee-ai-models`, custom domain `https://models.cproducts.dev`, layout `lyrics/v1/<file>[.partNN]` (64 MiB parts), object metadata (`Cache-Control: public, max-age=31536000, immutable`), immutability rule (a new pack is `lyrics/v2/` + `AiLyrics.PackVersion` bump + regenerated manifest; never overwrite), the runtime wheels come from PyPI by exact URL+hash, how to publish (wrangler/rclone commands with credentials from 1Password — no secret in the doc), how to regenerate the manifest, the smoke check, and the LyricsLab end-to-end command.
- `APP/ops/ai/lyrics-pack.v1.json`: the manifest source of truth (today's `LAB/upload/files.json` + runtime entries, i.e. the JSON currently embedded).
- `APP/ops/ai/New-AiLyricsPackManifest.ps1 -Manifest lyrics-pack.v1.json -Out src/apps/Wavee/AiLyrics/AiLyrics.PackManifest.cs`: emits the generated file (header comment, raw string). Deterministic output; running it twice is a no-op.
- `APP/ops/ai/Test-AiLyricsPack.ps1 [-Full]`: for every part: `HEAD` → 200, `Content-Length == bytes`, `Cache-Control` contains `immutable`; `GET Range: bytes=0-1023` → 206 with a `Content-Range`; for wheels: HEAD 200 + size. `-Full` streams every object and verifies SHA-256 of parts and of the reassembled file (≈1.3 GB). Non-zero exit on any mismatch; prints a table. `APP/ops/ai/tests/AiLyricsPack.Tests.ps1` (Pester, offline): manifest schema (every file has name/bytes/sha256/parts; parts sum to bytes; hex hashes; base ends with `/`; URLs https), and the generator's output round-trips (`New-… | Compare` against the committed `.cs`). Hook into `Invoke-Pester -Path ops/release/tests`? Keep `ops/ai/tests` separate and mention it in the guide; add a line to `releasing-wavee.md` gates only if the owner wants it as a release gate.

---

## 8. Ordered implementation steps (small, buildable; `[P]` = parallel subagent on disjoint files; orchestrator builds/tests)

**Step 0 — make the branch compile as-is (orchestrator).** `AiLyrics.Pack.cs` references the missing `Rules.FormatBytes`/`Rules.SpeedMeter`; stub `AiLyrics.Rules.cs` with `SpeedMeter` and switch `Pack.cs` to `Settings.StorageFormat.Bytes`. Verify: `dotnet build Wavee.slnx` Debug + Release clean. Commit the plan rewrite + LyricsLab tool (public-safe) now so the worktree state is reproducible.

**Step E1 [P, engine worktree] — Devices pillar.** Files: `ENG/src/FluentGpu.WindowsApi/Devices/ComputeAdapterInfo.cs`, `ComputeAdapters.cs`, `WindowsApiInfo.cs`, csproj comment, `ENG/CLAUDE.md`, `docs/guide/windows-integration.md`, `ENG/src/FluentGpu.Windows.Tests/ComputeAdapterInfoTests.cs`, `WindowsApiSmoke.cs` (DevicesSuite). Verify: engine Debug+Release build, `dotnet test src/FluentGpu.Windows.Tests` Debug+Release, VerticalSlice "ALL CHECKS PASSED", `--windowsapi-smoke` prints the NPU line on the Snapdragon box. Engine commit on a branch; the app's `Directory.Build.props` resolves `$(EngineRoot)` to the sibling worktree, so the app sees it immediately.

**Step A1 [P] — Rules (pure).** `AiLyrics.Rules.cs` with every function in §2.1 + `APP/src/apps/Wavee.Tests/AiLyricsRulesTests.cs` (availability matrix incl. build 26099 vs 26100 and vendor ids; eligibility table per doc kind/toggles/language/kind/saver; ETA/speed math; prepare weights/fraction; error→key; header state table; idle unload). Verify: tests Debug+Release.

**Step A2 [P] — Results codec.** `AiLyrics.Results.cs` + `AiLyricsResultsTests.cs` (round trip; overlay keeps provider translation; hash mismatch → miss; line-count mismatch → miss; sweep order).

**Step A3 [P] — Authority and RowShape.** `Shell/Lyrics.cs` (`IsRicher` tie-breaks, `RowShape.ChangedRows`) + tests (`LyricsRulesTests` or new `LyricsAuthorityTests.cs`, `RowShapeTests.cs`). Verify tests Debug+Release (no UI touched yet).

**Step A4 [P] — DSP performance (`AiLyrics.Dsp.cs` rewrite) — measurable target: HUMBLE separation wall ≤ 1.5 × NPU time (≈ ≤ 11.5 s vs 45.2 s now; align unchanged ~7.5 s; total ≤ ~22 s).**
1. Replace the `System.Numerics.Complex` (double) recursive FFT with a float, split-layout (`re[]`,`im[]`) iterative mixed-radix plan: N = 3840 = 2^8·3·5 for the half-size real FFT of 7680 (and 60/15 for tests). Precompute per-stage twiddle tables and the digit-reversal permutation once; radix-4 and radix-2 butterflies over `Vector128<float>` (AdvSimd on ARM64) processing 4 complex lanes; radix-3/5 as small DFT kernels. No allocation per call.
2. `RealFft` even/odd packing in float; `Stft.Forward`: window multiply via `TensorPrimitives.Multiply`, reflect-pad only on the two edge frames; write bins directly into the `[bin*frames+frame]` planes.
3. `Stft.Inverse`: overlap-add with `TensorPrimitives.MultiplyAdd`; the window-square normalization `_norm` depends only on (NFft, Hop, frames) → compute once per frame count, store its reciprocal, reuse.
4. `Separator`: two `Stft` instances; run L/R forward STFT and L/R inverse STFT on the `"wavee-ai-dsp"` thread (or `Parallel.For(0,2)` with preallocated per-channel scratch) while the worker thread issues the NPU `Run` of the previous window — a 2-slot ring (`winL/R`, `_input`, `_output` ×2). Target: CPU STFT+iSTFT of one window ≤ 150 ms total → fully hidden behind the ~245 ms NPU call.
5. `Resampler.Process`: precompute per-phase tap vectors (`Up` phases × taps/Up) and use `TensorPrimitives.Dot`; `Envelope.Append` vectorized sum of squares.
Tests `AiLyricsDspTests.cs`: FFT vs naive DFT at 15, 60, 3840 (double reference, tolerance 1e-3 relative); real FFT vs complex FFT; STFT→iSTFT round trip on a 261,120-sample chirp (max abs error < 1e-4); resampler length == scipy formula and 1 kHz sine amplitude within 1%; envelope dB of a known RMS. Bench: `Wavee.LyricsLab --bench-dsp` prints ms per window for Forward/Inverse/Resample. Accuracy gate: `Wavee.LyricsLab --ai` on HUMBLE/Despacito/Teen Spirit must keep "word-synced lines" and the lab's within-300 ms rates within ±1% of `LAB/cs-run.log` and the python results.

**Step D1 [P] — ops + guide.** `ops/ai/*`, `docs/guide/ai-lyrics-pack.md`, regenerate `AiLyrics.PackManifest.cs` from the JSON (byte-identical to today's). Verify: `Invoke-Pester -Path ops/ai/tests`; `Test-AiLyricsPack.ps1` → all green against the live bucket (already verified by hand; this makes it repeatable).

**Step B1 — platform plumbing (after A1).** `Platform.Keys` + `Prefs.AiLyrics` + `Platform.Power.EnergySaver`; `AiLyrics.cs` signals/verbs skeleton (`SetEnabled`, `StartDownload`, `PauseDownload`, `ResumeDownload`, `CancelDownload`, `Retry`, `RemoveFiles`, `InstallLanguage`, `RemoveLanguage`, `OpenFolder`). Build Debug+Release.

**Step B2 — Store seam.** `Lyrics.Store.Upgrade` + `LyricsHostTests` fact (synchronous `ToUi` default: an Upgrade for an unanswered id is dropped; for an answered id it bumps `Changed` and `Upgraded` and `Doc()` returns it; a non-Generated doc is refused).

**Step B3 — Host (after E1, A1, A2, B1, B2).** `AiLyrics.Host.cs`: boot Task (DXCore via `FluentGpu.WindowsApi.Devices.ComputeAdapters`, installed scan, pending removal, results sweep), `AiLyrics.Driver` component, worker thread, download Task with pause/resume (resume = re-run `InstallAsync`, which already resumes from `.partial`), prepare progress, results read/write, `Shutdown`. Delete `AiLyrics.Capability.cs`. Wire `AiLyrics.Install(post)` in `Shell.Host.InstallMarshallers` (after `Lyrics.Store.ToUi = post`), `AiLyrics.Shutdown()` in `Run`'s finally after `Playback.Os.Shutdown()`, and mount `AiLyrics.Driver()` in the shell root next to the ambient-power `UseInterval` host (grep `TickAmbientPower` in `Shell/Shell*.cs`). Headless hosts (`Diagnostics.Headless`, LyricsLab) do not install it. Verify: Debug+Release build; run with a scratch profile; log shows `ai.capability` and phase transitions; no UI-thread file I/O (check the diagnostics page's UI-thread stall counters stay at 0 during download).

**Step C1 [P after B1/B3 signatures] — Settings card** (`AiLyrics.UI.Settings.cs`, catalog rows, Appearance mount, Privacy row). **C2 [P] — rail sparkle + footer strip + per-row keys** (`AiLyrics.UI.Lyrics.cs`, `Rail.UI.cs`, `Lyrics.UI.cs`). **C3 [P] — diagnostics** (`AiLyrics.Diag.cs`, `Diagnostics.UI.cs`, debug overlay row). **C4 [P] — loc keys** (`en-US.json`; every key referenced). Verify: Debug+Release (FLLOC analyzers), `SettingsCatalogTests`, then the manual pass in §9.

**Step I1 — integration on the Snapdragon box.** Scratch profile run: enable → metered/size confirm → download with pause/cancel/resume → Preparing with per-graph progress (expect ~60 s for en) → Ready toast → play HUMBLE with the rail open → lines gain the wipe at handoffs with no scroll jump → header sparkle active → footer strip → inspector card; replay → instant from results cache; battery saver toggle pauses/resumes; Remove → deferred removal message → restart clears. Also `Wavee.LyricsLab --ai` timing table before/after A4 pasted into the plan's results section.

**Step M1 — metadata** (CHANGELOG with the approved `#n`, PRIVACY.md, notices-extra.json, store onboarding, plan doc rewrite).

**Final gates:** APP `dotnet build Wavee.slnx` Debug + Release; `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj` Debug + Release; ENG Debug + Release build, `FluentGpu.Windows.Tests` Debug + Release, VerticalSlice, `--windowsapi-smoke`; `Invoke-Pester ops/ai/tests`; PlayPlay paths untouched (`git status` shows nothing under the private patterns). Ask before any push; never stop the owner's running Wavee.

---

## 9. Test plan, risks, out of scope

**Tests (pure, no NPU/network, Debug+Release)**: `AiLyricsRulesTests`, `AiLyricsResultsTests`, `AiLyricsDspTests`, `AiLyricsAlignTests` (synthetic emissions → exact word frames; optional word skipped vs timed; prior windows; monotone commits across rounds; `Fill`; `WordSyncedLine` joins back to the text), `AiLyricsPackTests` (embedded manifest parses, every file has size+hash, `FilesFor(["en"])` excludes es, `DiskNeed`, segment/resume offset math, zip entry selection on an in-memory `ZipArchive`), `LyricsAuthorityTests` (§3.2), `RowShapeTests`, `LyricsHostTests` (`Store.Upgrade`), `SettingsCatalogTests` (automatic), engine `ComputeAdapterInfoTests`. End-to-end on device (not CI): `Wavee.LyricsLab --ai` on the 3 lab tracks + the manual pass I1. Pester: `ops/ai/tests`.

**Risks and mitigations**
- Whole-list remount flicker on progressive publishes → §3.4 per-row keys; verified by eye in I1 and by `RowShapeTests`.
- First-run compile (≈60 s for en, ≈100 s with es) → explicit "Optimizing for your NPU (one time)" with per-graph progress and ETA, cancellable between graphs, toast on completion; warm-load at app start when enabled so the first song never waits.
- Driver/runtime update invalidates `*.ctx.onnx` → existing stale-cache retry + `Recompile` flag in the UI.
- Two Wavee instances (owner's live + a scratch profile) compiling the same ctx file → write ctx to `<model>.ctx.<pid>.tmp` then `File.Move` (atomic), ignore a move that loses the race (ORT writes the file itself via `ep.context_file_path`; point it at the temp path, then move the pair `.ctx.onnx` + `_qnn.bin`; verify ORT's bin naming before relying on it — if ORT hard-codes the bin name next to the ctx, fall back to a `.lock` file).
- Memory: 11 NPU graphs resident (~1.3 GB of context on disk; measure RSS in Settings ▸ About) → idle unload after 10 min; only installed languages are loaded; measure and record in the plan.
- DSP target missed → the two-thread overlap alone hides CPU work up to the NPU time; the SIMD FFT is what gets below it. Keep the old `Dsp` as a test reference until A4 passes the accuracy gate, then delete it (no legacy paths).
- Provider doc changes between sessions → `sourceHash` guard.
- Disk full during compile (ctx files ≈ model size) → `DiskNeed` already budgets 2× models; `Compile` error maps to `error.compile` with the ORT message in Details.
- Metered mid-download → no auto-pause (user confirmed); the Downloading card shows the metered status line via `Settings.MeteredStatusLine` if desired.
- `NpuVendor == ""` in `cs-run.log` (ORT reports an empty vendor for the QNN plugin device) → never gate on ORT's vendor string; the DXCore vendor id is the gate, ORT's device type (NPU) is only checked for presence.

**Out of scope (this change)**: Intel/AMD NPUs and Windows ML's provider catalog; CPU/GPU execution providers and x64; languages beyond en/es; transcription of lyric-less tracks; podcasts/video; prefetch-aligning every played track (follow-up toggle `lyrics.ai.prefetch`); a push-based energy-saver notification in the engine; a Settings section anchor for the deep link; a Storage-tab usage-bar category for the models (a plain row is fine).

### Critical Files for Implementation
- `C:\wavee\fgpu\.claude\worktrees\lyrics-align\WaveeMusic\src\apps\Wavee\AiLyrics\AiLyrics.Host.cs` (new: driver, worker, download, results, shutdown)
- `C:\wavee\fgpu\.claude\worktrees\lyrics-align\WaveeMusic\src\apps\Wavee\AiLyrics\AiLyrics.Rules.cs` (new: every pure decision)
- `C:\wavee\fgpu\.claude\worktrees\lyrics-align\fluent-gpu\src\FluentGpu.WindowsApi\Devices\ComputeAdapters.cs` (new engine pillar; replaces `AiLyrics.Capability.cs`)
- `C:\wavee\fgpu\.claude\worktrees\lyrics-align\WaveeMusic\src\apps\Wavee\Shell\Lyrics.cs` (`Authority.IsRicher` tie-breaks, `RowShape.ChangedRows`) with `Shell\Lyrics.Host.cs` (`Store.Upgrade`) and `Shell\Lyrics.UI.cs` (per-row keys, footer mount)
- `C:\wavee\fgpu\.claude\worktrees\lyrics-align\WaveeMusic\src\apps\Wavee\AiLyrics\AiLyrics.Dsp.cs` (performance rewrite with the measurable ≤ 1.5× NPU target)