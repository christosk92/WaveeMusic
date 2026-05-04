# Wavee.Playback.Contracts — IPC contracts for the audio process

DTOs and named-pipe transport that define the wire protocol between `Wavee.UI.WinUI` (and the core `Wavee` library) and the out-of-process audio runtime `Wavee.AudioHost`.

`net10.0` · AOT-compatible (`IsAotCompatible=true`, `EnableTrimAnalyzer=true`) · `AnyCPU;ARM64;x64`. Zero dependencies (only `Microsoft.Extensions.Logging.Abstractions`).

## What's in here

| File                   | Role                                                                                          |
|------------------------|-----------------------------------------------------------------------------------------------|
| `IpcMessages.cs`       | All command / event DTOs: `IpcMessage` envelope, `PlayResolvedTrackCommand`, `PrepareNextTrackCommand`, …  |
| `IpcPipeTransport.cs`  | Length-prefixed JSON framing over a `NamedPipeServerStream` / `NamedPipeClientStream`.        |
| `AudioFileCache.cs`    | Shared file-cache contract (CDN bytes the audio process reads / writes).                      |

Frame format: `[4 bytes big-endian length][UTF-8 JSON payload]`. Each `IpcMessage` carries a `type` discriminator, a request `id` (for correlated request/reply), and a free-form `payload` JsonElement.

## Two consumption modes

This project is consumed in two different ways depending on which side of the pipe is reading:

1. **As a project reference** by `Wavee` (and indirectly by `Wavee.UI.WinUI`). Standard `<ProjectReference>` + `Wavee.Playback.Contracts.dll` at runtime.
2. **As source-included** by `Wavee.AudioHost`:

```xml
<!-- in Wavee.AudioHost.csproj -->
<Compile Include="..\Wavee.Playback.Contracts\IpcMessages.cs">
    <Link>Ipc\IpcMessages.cs</Link>
</Compile>
```

Why two modes: `Wavee.AudioHost` deliberately keeps **zero project references on Wavee\* assemblies** to avoid a stale-DLL bug class where `Wavee.Playback.Contracts.dll` could land next to `Wavee.AudioHost.exe` out of sync with the version the UI process is sending. The wire format is JSON, so type identity across assemblies doesn't matter — both ends just see `{type, id, payload}` strings.

## Adding a new command / event

1. Add a class to `IpcMessages.cs` decorated with `[JsonPropertyName(...)]` for every field. Use `required` for non-nullable contract fields.
2. Pick a `type` discriminator string (kebab-case is the convention).
3. Both the UI and AudioHost sides will see the new file automatically — UI via project ref, AudioHost via `Compile Include`.
