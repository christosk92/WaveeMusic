# Wavee.PlayPlay.Tests — PlayPlay decryption tests

Isolated test exe for the PlayPlay key emulator. Lives in its own project because the test host has to be x64.

`net10.0` · `OutputType=Exe` · **x64 only** (`<Platforms>x64</Platforms>`, `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`) · `AllowUnsafeBlocks=true`.

## Why x64-only

`PlayPlayKeyEmulator` (in `Wavee.AudioHost`) calls `LoadLibrary` on `Spotify.dll`, which is x86_64-native. It must run in the same process as the loader, so the test exe is forced to x64. On ARM64 Windows, it runs under the OS's built-in x64 emulation.

## Why a separate project from `Wavee.Tests`

`Wavee.Tests` is `AnyCPU;x64;ARM64` and references the protocol library only. Forcing it to x64 to accommodate PlayPlay would block running the rest of the suite on ARM64 / AnyCPU. Splitting keeps the two test suites independent.

## Public clones get a stub

The real test harness in `Program.cs` is **gitignored** because it bundles upstream test vectors that are Spotify property. Public clones get `Program.Stub.cs` instead. Conditional compile in the csproj:

```xml
<ItemGroup Condition="Exists('Program.cs')">
  <Compile Remove="Program.Stub.cs" />
</ItemGroup>
```

So:
- **With the real `Program.cs`**: actual decryption tests run against bundled vectors.
- **Without it (the open-source repo state)**: the stub `Program.cs` builds and runs but doesn't exercise PlayPlay end-to-end.

This is not xUnit-based — it's a plain `Exe` harness that exits non-zero on failure.

## Project refs

`Wavee.AudioHost` only. The host owns the `PlayPlayKeyEmulator` class; we exercise it in-process to avoid an IPC round-trip per test case.

## Run

```bash
dotnet run --project Wavee.PlayPlay.Tests -p Platform=x64
```
