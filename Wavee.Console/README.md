# Wavee.Console — AOT-compiled CLI

A terminal Spotify Connect client built on the same `Wavee` core as the WinUI app. Useful for headless control, smoke-testing the protocol layer, and reproducing Connect bugs without dragging the UI in.

`net10.0` · `OutputType=Exe` · **Native AOT** (`PublishAot=true`, `IlcOptimizationPreference=Size`, `IlcTrimMetadata=true`, `StripSymbols=true`) · `AnyCPU;ARM64;x64` · Linux-friendly (`DockerDefaultTargetOS=Linux`, ships a `Dockerfile`).

The csproj escalates every IL2xxx and IL3xxx AOT/trim warning to an error — if you add code that isn't trim-safe, the build fails until you annotate or refactor.

## What it does

`Program.cs`:

1. Configures Serilog through a `SpectreUI` sink so log lines render in the live TUI.
2. Sets up `IHttpClientFactory` and a DPAPI-backed `CredentialsCache`.
3. Generates / loads a stable device id, builds a `SessionConfig` (`DeviceName = "Wavee Console"`, `DeviceType = Computer`).
4. If a stored credentials blob exists, reuses it. Otherwise runs an OAuth flow (Authorization Code + PKCE) and stores the result.
5. Calls `Session.Create(...)` then `await session.ConnectAsync(credentials, credentialsCache)` with a status spinner.
6. Hands off to the interactive REPL in `ConnectConsole.cs`.

`ConnectConsole.cs` (~62 KB) implements the live UI: cluster state display, queue inspection, command issuing (play / pause / seek / next / prev / volume / transfer), device picker, etc.

## Files

```
Wavee.Console/
├── Program.cs              # Entry point: logging, OAuth, session connect, hand off to ConnectConsole
├── ConnectConsole.cs       # Interactive REPL — Connect commands and live cluster view
├── SpectreUI.cs            # Spectre.Console live-rendering host
├── SpectreLogSink.cs       # Serilog → Spectre live region sink
├── Dockerfile              # Linux container build
└── Wavee.Console.csproj
```

## Run

```bash
dotnet run --project Wavee.Console
```

Native publish (smaller, no .NET runtime needed):

```bash
dotnet publish Wavee.Console -c Release -r win-x64
dotnet publish Wavee.Console -c Release -r linux-x64
dotnet publish Wavee.Console -c Release -r linux-arm64
```

Docker:

```bash
docker build -t wavee-console -f Wavee.Console/Dockerfile .
docker run -it --rm wavee-console
```

## Dependencies

- `Spectre.Console` — TUI.
- `Microsoft.Extensions.{DependencyInjection, Http, Logging.Console}` — DI + HTTP + logging.
- `Serilog` (+ `Serilog.Extensions.{Hosting, Logging}`, `Serilog.Sinks.Console`) — structured logs.
- Project refs: `Wavee` (protocol), `Wavee.AudioHost` (referenced for build ordering — actual playback still runs out-of-process in production layouts).
