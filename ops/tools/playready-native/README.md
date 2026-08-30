# FluentGpu.PlayReady.Native

In-process Win32 native DLL for PlayReady-protected video via Media Foundation CDM/CENC. This is **not** a UWP sidecar or cross-process helper — the managed engine loads `FluentGpu.PlayReady.Native.dll` directly.

## Build

From this directory, with Visual Studio C++ tools installed:

```cmd
build.cmd arm64
build.cmd x64
```

Output: `out/{arch}/FluentGpu.PlayReady.Native.dll` (e.g. `out/arm64/FluentGpu.PlayReady.Native.dll`).

## Managed code

The C# integration lives in `src/FluentGpu.WindowsApi/Media/PlayReady/` (`DesktopProtectedVideoPlayer`, `ProtectedMediaSession`, `DashManifestParser`, etc.). See [`docs/guide/playready-native.md`](../../../docs/guide/playready-native.md) for the end-to-end guide.

## Sources

| File | Role |
|---|---|
| `PlayReadyNative.cpp` | Desktop PMP bridge + in-process CDM/CENC media source |
| `CencMediaSource.h` | Custom `IMFMediaSource` emitting encrypted CENC samples |
| `build.cmd` | MSVC build (`FG_UWP` + `FG_WIN32_PMP` + `FG_DESKTOP_DLL`) |
