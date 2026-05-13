# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Agent component docs

Component-specific, LLM-friendly guides live in `.agents/guides/`. Read the relevant guide before touching that area:

- `.agents/guides/track-and-episode-ui.md` — every track/episode row/list/card/search/queue/now-playing surface.
- `.agents/guides/connect-state.md` — Spotify Connect: dealer WebSocket, this-device announce (PutState), cluster state, remote commands (Play/Pause/Seek/Transfer/SetVolume/Queue), device picker / volume / now-playing UI.
- `.agents/guides/library-and-sync.md` — User library lifecycle: collection sync (tracks / albums / artists / shows / pins / listen-later), playlist cache, dealer-driven incremental updates, save / pin / follow write paths, library and pinned UI surfaces.

When adding a new component guide, follow the frontmatter, Quick-find table, and authoring conventions in `AGENTS.md` (single source of truth), and update the index in both `AGENTS.md` and this file.

## Repository at a glance

WaveeMusic is a clean-room Spotify desktop client for Windows: a .NET 10 reimplementation of Spotify's Access Point, Mercury, Connect (Dealer WebSocket), SpClient (protobuf/HTTPS), and Pathfinder (GraphQL) protocols, wrapped in a WinUI 3 app. Requires a Spotify Premium account.

Top-level project READMEs are the canonical source of architectural detail — they're already detailed and current. Read them before invasive changes:

- `README.md` — features, project layout, gabo events surface, what's excluded from public source.
- `src/Wavee/README.md` — core protocol library entry points (`Session`, `DealerClient`, `PlaybackOrchestrator`, `SpClient`, `PathfinderClient`, `AudioKeyManager`).
- `src/Wavee.UI/README.md` — framework-neutral UI service layer.
- `src/Wavee.UI.WinUI/README.md` — desktop app composition, services, custom MSBuild targets, on-device AI plumbing.
- `src/Wavee.AudioHost/README.md` — out-of-process audio runtime (x64-only).
- `src/Wavee.Playback.Contracts/README.md` — IPC wire format.
- `src/Wavee.Console/README.md` — AOT CLI client (Linux/Docker friendly).
- `src/Wavee.Controls.Lyrics/README.md` — lyrics rendering control library.
- `src/Wavee/Connect/DEALER_PROTOCOL.md` and `DEALER_IMPLEMENTATION_GUIDE.md` — wire-level Dealer reference.
- `src/Wavee/OAuth/OAUTH_FLOWS.md` — Spotify OAuth analysis.

## Common commands

All commands run from the repo root.

```bash
# Build
dotnet build                       # debug
dotnet build -c Release

# Run desktop client
dotnet run --project src/Wavee.UI.WinUI

# Run console client
dotnet run --project src/Wavee.Console

# Run AudioHost manually for diagnostics (refuses to start without --standalone-dev)
dotnet run --project src/Wavee.AudioHost -p Platform=x64 -- --standalone-dev --pipe MyPipe --verbose

# Tests
dotnet test                                                              # whole solution
dotnet test test/Wavee.Tests/Wavee.Tests.csproj                               # core suite
dotnet test test/Wavee.UI.Tests/Wavee.UI.Tests.csproj                         # UI service layer
dotnet test test/Wavee.Tests/Wavee.Tests.csproj --filter "FullyQualifiedName~ShannonCipher"   # single class
dotnet test test/Wavee.Tests/Wavee.Tests.csproj --filter "FullyQualifiedName~Wavee.Tests.Connect"  # namespace

# PlayPlay decryption tests (x64-only, plain Exe harness — exits non-zero on failure, not xUnit)
dotnet run --project test/Wavee.PlayPlay.Tests -p Platform=x64

# Native publish for Wavee.Console
dotnet publish src/Wavee.Console -c Release -r win-x64
dotnet publish src/Wavee.Console -c Release -r linux-x64
dotnet publish src/Wavee.Console -c Release -r linux-arm64
```

Prerequisites: .NET 10 SDK, Windows 11 24H2 (build 26100) or later for the WinUI app, Spotify Premium account.

## High-level architecture

### Process model

The desktop app runs as **two processes** that talk over a named pipe:

```
Wavee.UI.WinUI  (any arch)  ◄── named pipe ──►  Wavee.AudioHost  (x64-only)
```

`Wavee.UI.WinUI`'s `BuildAudioHost` MSBuild target spawns an **isolated** `dotnet build` subprocess for `Wavee.AudioHost` on every WinUI build (with `Platform=x64` forced). This is deliberate, not a quirk: an in-process MSBuild reference would inherit the parent build's project-evaluation cache and could land an ARM64 NVorbis.dll next to AudioHost's x64 exe. The fresh subprocess prevents that whole class of bug. Cost: a few seconds per launch.

AudioHost is x64-only because it `LoadLibrary`s `Spotify.dll` (x86_64-native) for PlayPlay key derivation. On ARM64 Windows it runs under built-in x64 emulation. The WinUI process can be any arch.

### Why a separate audio process

1. Audio engine crashes don't take the UI down; UI can respawn the host.
2. Audio doesn't pay for the WinUI process's reflection-heavy MVVM heap; UI doesn't pay for native audio buffers.
3. Per-process arch isolation (see above).

### IPC contract

Length-prefixed JSON over named pipe: `[4 bytes big-endian length][UTF-8 JSON payload]`. Each `IpcMessage` carries `type` (kebab-case discriminator), `id` (request correlation), and `payload` (free-form `JsonElement`).

`Wavee.Playback.Contracts` is consumed two different ways on purpose:
- **Project reference** by `Wavee` and (transitively) `Wavee.UI.WinUI`.
- **Source-included** by `Wavee.AudioHost` (`<Compile Include="..\Wavee.Playback.Contracts\IpcMessages.cs">`). AudioHost has **zero project references on Wavee\* assemblies** so a stale `Wavee.Playback.Contracts.dll` can never land alongside `Wavee.AudioHost.exe` and break startup. JSON wire format means type identity across assemblies doesn't matter.

When adding a new IPC command/event: edit `src/Wavee.Playback.Contracts/IpcMessages.cs` only. Both sides pick it up automatically.

### Project layering

```
Wavee.UI.WinUI  ──►  Wavee.UI            (framework-neutral, plain C# — testable without WinUI)
                ──►  Wavee                (core protocol library, no UI)
                ──►  Wavee.Playback.Contracts
                ──►  Wavee.Controls.Lyrics
                ──►  Lyricify.Lyrics.Helper (vendored)

Wavee  ──►  Wavee.Playback.Contracts
       ──►  NVorbis (vendored Ogg decoder)

Wavee.AudioHost  ──►  NVorbis only (no Wavee* refs — see IPC section)
```

`Wavee.UI` exists so anything that doesn't strictly need WinUI can be tested directly via `Wavee.UI.Tests` without booting the UI framework. Both `Wavee.UI` and `Wavee.UI.WinUI` get `InternalsVisibleTo` from `Wavee.UI`.

### Core library entry point — `Session`

`src/Wavee/Core/Session/Session.cs` is the anchor of everything: AP TCP+TLS connect, authenticate, then **lazy-init** of every subsystem (Dealer, Mercury, Pathfinder, AudioKeyManager, EventService) on first access. A consumer that only needs metadata (`pathfinder.SearchAsync(...)`) never opens a Dealer WebSocket or audio key channel. Use `ISession` in DI; the WinUI app registers a singleton wired with `SessionConfig`, `IHttpClientFactory`, `ILogger<Session>`, and an optional `IRemoteStateRecorder` (off by default; toggled from the Debug page).

Most state across the protocol layer is exposed as `IObservable<T>` — `System.Reactive` is used heavily.

### WinUI app composition

`App.OnLaunched` → `AppLifecycleHelper.ConfigureHost()` builds an `IHost` (Microsoft.Extensions.Hosting) → `Ioc.Default.ConfigureServices(...)` wires CommunityToolkit MVVM `Ioc` to the same container → force-resolve `IMetadataDatabase` to run schema migrations before first paint → start `CacheCleanupService` → activate `MainWindow` → `InitializeApplicationAsync` runs deferred init (login state, library sync). Crash handlers are wired at three levels (XAML, AppDomain, unobserved-task scheduler) and funnel through `LogUnhandledException` → `PiiRedactor` → `AppPaths.CrashLogPath`.

### Spotify Connect / Dealer

`src/Wavee/Connect/` implements the full Dealer WebSocket protocol (cluster state, transfer, volume, remote commands). `DealerClient` exposes `IObservable<DealerMessage>` and `Requests`. `DeviceStateManager` announces this device and handles `PutState`. `PlaybackStateManager` is the reactive view of remote cluster state. `ConnectCommandClient` issues commands with ack tracking. The desktop client works as both controller and target.

### Telemetry (gabo events)

Wavee posts the bare minimum playback events Spotify needs to credit Recently Played and play counts. All events go to `https://spclient.wg.spotify.com/gabo-receiver-service/v3/events/`. The legacy `event-service/v1/events` path returns 404 — gabo is the only working transport.

The `GaboEnvelopeFactory` mimics the C++ desktop client's context block (SDK strings, app version `1.2.88.483`, real BIOS manufacturer/model + Windows machine SID) to clear Spotify's anti-fraud pipeline. Anti-fraud silently drops batches whose context block doesn't look first-party. **If you change SDK strings, version code, or the device-context fragments, expect Recently Played to silently stop working.** The breakthrough is documented inline in `src/Wavee/Connect/Events/GaboEnvelopeFactory.cs`.

### AOT / trimming

`Wavee` and `Wavee.Console` are **strict AOT-compatible**: every IL2xxx/IL3xxx warning is escalated to an error. Adding reflection-based code will fail the build until it's annotated or refactored. Public types in `Wavee` are kept (no runtime reflection codegen). Protobuf-generated code is `Access="Public"` and excluded from default `Compile` to avoid duplication.

`Wavee.UI.WinUI` is **not** AOT/trim — it's framework-dependent, and re-enabling either would need `<SelfContained>true</>` (~70-100 MB AppX bloat) plus trim-validation across WinUI/ReactiveUI/MVVM Toolkit/ComputeSharp.

## What's excluded from the public source tree (proprietary)

Three Spotify-property files are gitignored. The build still compiles via stub fallbacks; specific features degrade.

| File | Effect when missing |
|---|---|
| `src/Wavee/Core/Crypto/AudioDecryptStream.cs` | No stub — file simply absent. Cannot decrypt encrypted Spotify Ogg streams in this repo's open form. Test fixtures in `test/Wavee.Tests/Core/Crypto/` document what *would* be tested. |
| `src/Wavee/Core/Audio/PlayPlayConstants.cs` | `PlayPlayConstants.Stub.cs` is used (csproj swaps via `Condition="Exists(...)"`). PlayPlay key fallback disabled at runtime; `AudioKeyManager` falls back to AP-only key resolution. |
| `src/Wavee.AudioHost/PlayPlay/PlayPlayKeyEmulator.cs` | `PlayPlayKeyEmulator.Stub.cs` ships in its place. `Wavee.PlayPlay.Tests` runs against the stub and skips real test vectors. |
| `test/test/Wavee.PlayPlay.Tests/Program.cs` | `Program.Stub.cs` ships in its place; the harness builds and runs but doesn't exercise PlayPlay end-to-end. |

Everything else works without these: auth, session management, Spotify Connect (controller + target), all metadata APIs, UI, library sync, search, lyrics, music videos via PlayReady, Connect commands, telemetry.

## Audio runtime support pack provisioning

The PlayPlay key-derivation path needs an x86_64 PE binary on disk that
`PlayPlayKeyEmulator` can `LoadLibrary` and call into (`vm_runtime_init` +
`vm_object_transform` at config-supplied RVAs). On a fresh install, that
binary is **not** present — it's not bundled with WaveeMusic.

`Wavee.Core.Audio.AudioRuntimeProvisioner` is the first-run hook that fetches
the binary. The flow:

1. `AppLifecycleHelper.InitializeOutOfProcessAudioAsync` constructs an
   `AudioRuntimeProvisioner` with the shared `HttpClient`.
2. The provisioner GETs `https://cproducts.dev/r/manifest.json`, falling back
   to a disk-cached copy at `%LOCALAPPDATA%\Wavee\PlayPlay\manifest.json` if
   the network fetch fails.
3. The manifest lists active runtime packs in priority order. For each pack
   the provisioner checks
   `%LOCALAPPDATA%\Wavee\PlayPlay\packs\<pack-id>\Spotify.dll` for a SHA-256
   match; on miss it GETs `pack.url`, XOR-unwraps the response with a key
   derived from a fixed phrase, verifies SHA-256, and atomic-writes to the
   per-pack cache directory.
4. On success, the provisioner returns a `RuntimeAsset(path, config, packJson)`
   record. `AudioHostPlayPlayKeyDeriver` registers itself with the session
   using that asset; the path and the pack's serialised JSON go over the IPC
   pipe to AudioHost on every derivation request, where `AudioHostService`
   parses the JSON, builds the `PlayPlayConfig`, and lazily instantiates a
   `PlayPlayKeyEmulator(path, config, logger)`.
5. On any failure the deriver simply isn't registered; `AudioKeyManager`
   falls back to AP-only key resolution and the app loads normally.

All version-specific configuration — token bytes, expected SHA, RVAs, init
value, AES extraction strategy — lives in the manifest, not in WaveeMusic
source. New runtime packs are published server-side without a client redeploy.

The operator-side tooling (wrap script, manifest upload, blob URL
management) lives at `C:\WAVEE\wavee-dev-helpers\` — a sibling directory,
not part of this repo. See its `AGENTS.md` for the rotation runbook.
Do **not** commit anything from that directory into this repo, and keep new
code in this codebase neutral about what the support pack actually contains
(generic terms like "audio runtime support pack" rather than naming any
upstream binary). When editing PlayPlay-touching code, commit messages and
doc comments stay neutral too.

## Custom MSBuild targets to be aware of

In `Wavee.UI.WinUI.csproj`:

- **`BuildAudioHost`** — see "Process model" above. Spawns an isolated `dotnet build` for `Wavee.AudioHost` with `Platform=x64`. Required.
- **`RemoveDuplicateReferencedProjectAssets`** — WinUI AppX packaging copies `<Content>` from referenced projects twice. This deletes specific orphan duplicates (~14.6 MB for `Wiki82.profile.xml` alone). **Do not** widen the deletion to the full `AppX\Wavee.Controls.Lyrics\` folder — it also contains compiled `.xbf` per-assembly XAML that WinUI resolves at runtime.
- **`StripUnusedWindowsAiPayload`** — currently a no-op; ships the full Windows AI payload (~42 MB) until safe-to-strip DLLs are empirically verified on a real Copilot+ PC.
- **`IncludeWindowsAiProjectionAssembliesInMsixPayload` / `CopyWindowsAiProjectionAssembliesToAppxLayout(AfterPri)` / `PreserveWindowsAiManifestMaxVersionTested`** — workaround a WinAppSDK 2.0.1 regression that doesn't deploy `Microsoft.Windows.AI.*.Projection.dll` into AppX, plus an MSIX tooling bug that rewrites manifest `MaxVersionTested` back to the installed Windows SDK version. **Delete these targets when WinAppSDK / MSIX tooling fixes both.**

## On-device AI (Phi Silica)

The desktop app uses Microsoft's NPU-tuned Phi Silica via `Microsoft.Windows.AI.Text.LanguageModel` for opt-in lyrics features (explain a line, summarize meaning). All inference is on-device.

- **Hardware-gated**: Copilot+ PC required. Hidden everywhere else; model never downloaded.
- **Opt-in by default**: master toggle in Settings → On-device AI is OFF. Until flipped, no AI affordance renders and no Phi Silica calls are made.
- **Region-gated**: not available in China; Settings shows "Not available in your region" and affordances stay hidden.
- **Limited Access Feature**: production builds need an LAF unlock token in `Package.appxmanifest`. Without it, `LanguageModel.CreateAsync()` throws — `AiCapabilities` swallows that and affordances stay hidden.
- **Single decision point**: `src/Wavee.UI.WinUI/Services/AiCapabilities.cs` composes hardware + region + opt-in. Every AI affordance binds against it.

## Testing

| Suite | What | Notes |
|---|---|---|
| `Wavee.Tests` | Core protocol library (`Wavee`) | xUnit v3 + FluentAssertions + Moq. `AnyCPU;x64;ARM64`. `DynamicProxyGenAssembly2` has `InternalsVisibleTo` for Castle proxy mocks. |
| `Wavee.UI.Tests` | Framework-neutral UI service layer (`Wavee.UI`) | xUnit v3 + FluentAssertions + Moq. Doesn't reference WinUI deliberately — that would drag in MSIX/RID complexity. |
| `Wavee.PlayPlay.Tests` | PlayPlay key emulator | Plain `Exe` harness, **not** xUnit; exits non-zero on failure. x64-only because it loads `Spotify.dll`. |

Crypto primitives (`ShannonCipher`, `AudioDecryptStream`) are validated against librespot vectors — see `test/Wavee.Tests/Core/Crypto/README.md` for how to regenerate.

## Conventions worth knowing

- **`InternalsVisibleTo` is part of the contract.** `Wavee` exposes internals to `Wavee.Tests`; `Wavee.UI` exposes to `Wavee.UI.WinUI` and `Wavee.UI.Tests`; `Wavee.UI.WinUI` exposes to `Wavee.Tests`. Marking something `internal` here is intentional, not a leak.
- **`IRemoteStateRecorder`** is threaded through `Session`, `DealerClient`, `DeviceStateManager`, `PlaybackStateManager`, and HTTP clients. Pass `null` for "no recording"; pass a real implementation (the WinUI app's `RemoteStateRecorder`, capped at 500 entries) to capture every dealer message + HTTP call for the Debug page.
- **DATAS GC** in `Wavee.UI.WinUI` (`<ServerGarbageCollection>true</>` + `<GarbageCollectionAdaptationMode>1</>`). Earlier the project used workstation GC; that exhibited a textbook mid-life-crisis (Gen0:Gen1:Gen2 ≈ 711:541:334) on image-heavy nav. DATAS starts with one heap (workstation-sized working set) and adapts heap count under sustained pressure (server-GC throughput). Audio pipeline is out-of-process, so UI GC pauses don't need to protect playback.
- **C# preview language version** in `Wavee.UI.WinUI` (required for CommunityToolkit.Mvvm 8.4.x on .NET 10).
- **Protobuf code generation** runs at build time (`Grpc.Tools` 2.80.0). Generated files land in `src/Wavee/Protocol/Generated/`, are excluded from default `Compile`, and the `Protobuf` ItemGroup re-includes them with `Access="Public"`.
