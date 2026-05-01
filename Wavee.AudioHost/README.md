# Wavee.AudioHost — out-of-process audio runtime

The audio engine. Spawned as a separate process by `Wavee.UI.WinUI` and talked to over a named pipe. Handles CDN download, Spotify track decryption (AES + PlayPlay fallback), Ogg Vorbis decode, mixing / DSP, and output.

`net10.0` · `OutputType=Exe` · **x64 only** (`<Platforms>x64</Platforms>`, `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`) · AOT-compatible (`IsAotCompatible=true`, `EnableTrimAnalyzer=true`).

## Why a separate process — and why x64

**Why x64**: `Spotify.dll` is x86_64-native. Wavee loads it directly to derive PlayPlay AES keys. The host process therefore must be x86_64 too. On ARM64 Windows the OS runs this exe under its built-in x64 emulation, and the WinUI process (any architecture) talks to it over the named pipe.

**Why a separate process**:
1. Audio engine crashes don't bring the UI down — the WinUI process can spawn a new audio host and keep going.
2. Audio doesn't pay for the WinUI process's GC heap shape (workstation GC, reflection-heavy MVVM stack), and the UI doesn't pay for native audio buffers in its working set.
3. Per-process arch isolation — see above.

## IPC

Length-prefixed JSON over a named pipe with request IDs. The pipe name is passed via `--pipe <name>` (defaults to `WaveeAudio`). Frame format: `[4 bytes big-endian length][UTF-8 JSON payload]`.

The contract types are **source-included** from `Wavee.Playback.Contracts`, not project-referenced:

```xml
<Compile Include="..\Wavee.Playback.Contracts\IpcMessages.cs">
    <Link>Ipc\IpcMessages.cs</Link>
</Compile>
<Compile Include="..\Wavee.Playback.Contracts\IpcPipeTransport.cs">
    <Link>Ipc\IpcPipeTransport.cs</Link>
</Compile>
<Compile Include="..\Wavee.Playback.Contracts\AudioFileCache.cs">
    <Link>Ipc\AudioFileCache.cs</Link>
</Compile>
```

This eliminates a class of bugs where a stale `Wavee.Playback.Contracts.dll` could land alongside `Wavee.AudioHost.exe` and cause `FileNotFoundException` at startup. The wire format is JSON, so type identity across assemblies doesn't matter — both ends just see `{type, id, payload}` strings.

## Audio stack

| Library              | Role                                                                  |
|----------------------|-----------------------------------------------------------------------|
| `ManagedBass`        | Decoder + mixer + DSP (EQ, normalization, crossfade)                  |
| `NVorbis`            | Managed Ogg Vorbis decoder (vendored project)                         |
| `PortAudioSharp2`    | Cross-platform audio output                                           |
| `z440.atl.core`      | Audio file metadata                                                   |

`PortAudioSharp2` ships no `win-arm64` native binary; `ManagedBass` ships no native binary at all. Both gaps are bridged at startup by `NativeDeps/`, which downloads `portaudio.dll` (ARM64) or `bass.dll` (x64) into the runtime directory if missing. Failure writes a marker file and exits with code 3 so the UI can distinguish "first-run setup failure" from a transient crash.

## Folder map

```
Wavee.AudioHost/
├── Program.cs              # Entry point: arg parsing, Serilog setup, native-dep bootstrap, run AudioHostService
├── AudioHostService.cs     # IPC server loop + audio session orchestration
├── PreviewAnalysisService.cs
├── Audio/                  # BASS / NVorbis / PortAudio glue
├── NativeDeps/             # Native dependency bootstrap (PortAudioWinArm64Descriptor, BassWinX64Descriptor)
└── PlayPlay/               # PlayPlay key emulator (Spotify property; gitignored)
```

## PlayPlay (Spotify property)

`PlayPlayConstants.cs` is gitignored. Two consumption modes via conditional `Compile`:

```xml
<ItemGroup Condition="Exists('..\Wavee\Core\Audio\PlayPlayConstants.cs')">
  <Compile Include="..\Wavee\Core\Audio\PlayPlayConstants.cs">
    <Link>PlayPlay\PlayPlayConstants.cs</Link>
  </Compile>
</ItemGroup>
<ItemGroup Condition="!Exists('..\Wavee\Core\Audio\PlayPlayConstants.cs')">
  <Compile Include="..\Wavee\Core\Audio\PlayPlayConstants.Stub.cs">
    <Link>PlayPlay\PlayPlayConstants.cs</Link>
  </Compile>
</ItemGroup>
```

When the real file is absent (the public-repo case), the stub is used and PlayPlay-based key derivation is disabled at runtime. The `AudioKeyManager` in `Wavee` falls back to the AP audio-key channel for everything.

`PlayPlayKeyEmulator.cs` follows the same shared-source pattern: `PlayPlayKeyEmulator.Stub.cs` is removed from compilation when the real `PlayPlayKeyEmulator.cs` is present.

## Process model

```
Wavee.UI.WinUI                       Wavee.AudioHost
  │                                       │
  │ Process.Start with:                   │
  │   --pipe <random>                     │
  │   --parent-pid <ui pid>               │
  │   --session-id <session guid>         │
  │   --launch-token <random>             │
  │ ─────────────────────────────────────►│
  │                                       │ Validates required args (refuses
  │                                       │ standalone start unless --standalone-dev),
  │                                       │ sets GC LatencyMode = SustainedLowLatency,
  │                                       │ bootstraps native deps,
  │                                       │ opens named pipe and serves AudioHostService.
  │ ◄═════════ named pipe ══════════════► │
  │                                       │
  │ Monitors AudioHost via a parent-      │
  │ monitor watch; if AudioHost dies,     │
  │ surfaces it in the Debug page and     │
  │ optionally respawns.                  │
```

## Build / run

You generally don't build this directly — `Wavee.UI.WinUI`'s `BuildAudioHost` MSBuild target spawns an isolated `dotnet build` of this project on every WinUI build. But if you want to run it manually for diagnostics:

```bash
dotnet run --project Wavee.AudioHost -p Platform=x64 -- --standalone-dev --pipe MyPipe --verbose
```

`--standalone-dev` is required for manual launch; otherwise the host refuses to start without `--parent-pid`, `--session-id`, and `--launch-token` from the UI.

## Dependencies

`ManagedBass`, `PortAudioSharp2`, `NVorbis` (project ref), `z440.atl.core`, `System.Reactive` (preview), `Microsoft.Extensions.{Logging, Logging.Console, Http}`, `Serilog` (Console + File sinks).

**Zero project references on Wavee\* assemblies.** All shared types come in via `Compile Include`.
