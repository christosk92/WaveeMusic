# Playback modules — where to change what

A **playback module** teaches Wavee to play a source it does not know (YouTube, Twitch, internet radio, and next
Spotify itself). Because the app is NativeAOT + `TrimMode full`, it cannot load a managed plugin: a module is an
**out-of-process exe** speaking JSON-RPC 2.0 over stdio, and the SDK hides the transport entirely.

Full reference (manifest, wire, author quick-start, publish layout, diagnostics, Spotify migration, research
appendix): **`docs/guide/playback-modules.md`**. This page is the file map.

## The three layers

| Layer | Path | What lives there |
|---|---|---|
| **SDK** (public contract) | `src/apps/Wavee.Sdk/**` | `ModuleManifest`, the playback DTOs (`MatchResult`, `ResolvedPlayable`, `MediaLocator`, `WireMeta`, `ResolvePreferences`), `ModuleUri`, `ModuleError*`, `WaveeModule`, `IModuleHost`, `IModuleStream`, `ModuleRunner`, `ModuleTestHost`, `Protocol/` (`JsonRpcFramer`, `JsonRpcConnection`, `ModuleMethods`, `ModuleProtocol`, `SdkJsonContext`), `Streams/` (`RangedHttpSource`, `RangeSet`, `ChunkDiskCache`), `Http/AuthenticatedHttp`. **Zero project references** — it is the NuGet a third party consumes. |
| **Host** (app side) | `src/apps/Wavee/Backend/Modules/**` | `ModuleCatalog` (discovery + `ModuleRejection` diagnostics), `ModuleProcess` (+ `ModuleProcessState`, `ModuleTimeouts`, `ChildProcessChannel` behind the `IModuleChannel`/`ModuleSpawn` test seam), `ModuleHost` (the façade `Services` composes), `ModuleMediaProvider` (one `IPlayableMediaProvider` per module), `ModulePlayableCache` / `ModulePlayables` (sync `HasVideo`/`IsLive`/`Get` answers), `ModuleRouter` + `ModuleCapabilities` (paste-url matching), `ModuleByteStream` (module-served bytes), `ModuleHostServices` + `IModuleSecretStore` (the module→host calls), `ModuleStats`. |
| **Modules** | `src/apps/modules/Wavee.Module.{YouTube,Twitch,Radio}/**` | One `net10.0` `Exe` each, `PublishAot=true`, references **only** `Wavee.Sdk`, ships `wavee-module.json`. Tests + recorded fixtures in `src/apps/Wavee.Tests/Modules/`. |

## The manifest

`wavee-module.json` sits next to the entry point and is the only thing the host reads before launching anything:
`{ schemaVersion, id, version, displayName, publisher, protocolVersion, entry, capabilities[], urlPatterns[],
menu{label, placeholder} }`. Shape = the `ModuleManifest` record. Discovery roots, highest compatible version wins:

- `<app dir>\modules\<id>\wavee-module.json` — bundled (the floor, never removed)
- `%LOCALAPPDATA%\Wavee\modules\<id>\<version>\wavee-module.json` — user store

Per-module writable data: `%LOCALAPPDATA%\Wavee\modules-data\<id>` (`WAVEE_MODULE_DATA_DIR` / `ModuleContext.DataDir`).
Playable uris are `wavee:module:<id>:<b64url(playableId)>` — always go through `ModuleUri.Encode/TryDecode/Prefix`,
never string surgery.

## Rules that are easy to break

1. **`entry` is `.dll` in dev and `.exe` in publish.** One host code path (`entry.EndsWith(".dll")` → run `dotnet`);
   the manifest decides. Never branch the host on "am I a dev build". Mechanism: each module checks in TWO files —
   `wavee-module.dev.json` (`TargetPath="wavee-module.json"`, output only) and `wavee-module.json` (publish only) —
   so exactly one lands beside the entry point either way.
2. **stdout is the protocol channel.** `ModuleRunner` repoints `Console.Out` at stderr before user code runs. Never
   write to the real stdout from a module; log via `Host.Log(...)` or stderr (it becomes `WaveeLog` category
   `module.<id>`).
3. **`-32601` (method not found) is "capability absent", never a failure.** Missing capability ⇒ the simpler proven
   path, exactly like `MediaProviderCaps`.
4. **Capabilities are declared, never probed** — manifest `capabilities` → `MediaProviderCaps` /
   `SourceCapabilities`; per-playable `ResolvedPlayable.Caps` (`preparedNext`, `connectPublish`, `wireMeta`).
5. **A source enters the app only through a registry.** Module providers are appended to `MediaProviderRegistry`
   after the built-ins; the **Play ▸** menu is built from `ModuleHost.Installed`, never a hard-coded list. A
   component never names `ModuleHost` — it reads it through `Services.Slot` / `ActionServices.Slot`.
6. **Every JSON shape goes through an STJ source-gen context** (`SdkJsonContext` for SDK types; a module owns one
   for its own). `IModuleHost.CallAsync` takes `JsonTypeInfo<T>` for both sides for exactly that reason.
7. **`durationMs: 0` means unknown** (live) — it keeps every ending-soon/gapless/crossfade arm off.
8. **The video host cannot set request headers.** Never resolve an HLS url that needs them.
9. **No env switches.** Diagnostics are always on; nothing about module behaviour is gated on an env var.

## Testing

- Module tests: `ModuleTestHost` calls the module class directly (no process, no network — inject an
  `HttpMessageHandler`, drive recorded fixtures). It records `Metadata` / `Expired` / `Status` / `Progress` /
  `Logs` and can stand in for host services (`TokenProvider`, `AuthContextProvider`, `SecretReader`/`Writer`,
  `CallHandler`).
- Host tests: an in-memory `JsonRpcConnection` pair against a scripted fake module (including one serving a local
  file over `stream/open|read|close`). Real processes only in `[Trait("Category","Integration")]`.
- Manual: `Wavee.Module.YouTube.exe match <url>` / `resolve <playableId>` print JSON to stdout (same code paths as
  `ModuleTestHost`); exit 0 = found, 1 = not found/failed, 2 = bad usage.

## Diagnostics

The existing playback-diagnostics page (setup dialog → **View diagnostics**) grows a **Modules** section: per
module the id/version/publisher/dir (bundled vs user store), capabilities, protocol version, process state, PID,
last error, and `ModuleStats` (requests, failures, p50/p95, restarts); **plus every probed directory that did not
load and why** (the PlayPlay diagnostics discipline — a rejected manifest is a visible row, never a silent skip);
plus whatever `module/diagnostics` returned and any `module/status` actions as buttons.

## Build & packaging

- Dev: `Wavee.csproj` `ProjectReference`s each module (`ReferenceOutputAssembly=false`, `Private=false`,
  `Exists()`-guarded — build ordering only, nothing links) and `CopyBundledModules` stages
  `src\apps\modules\Wavee.Module.<Name>\bin\<cfg>\net10.0\**` → `$(TargetDir)modules\<id>\`.
- Publish/MSIX: `ops/build/publish-wavee-modules.ps1` publishes each module NativeAOT into
  `<publish>\modules\<id>\`; both `publish-wavee-aot.ps1` and `pack-wavee-msix.ps1` call it, so the two layouts
  cannot drift.
- Distribution: `ops/build/pack-module.ps1 -ModuleDir <dir> -OutputDir <dir> [-Arch arm64]` → zip + `.sha256`
  sidecar + a printed `modules.json` feed entry.
- Solution: the three module projects live in `src/FluentGpu.slnx` under `/Apps/`.
