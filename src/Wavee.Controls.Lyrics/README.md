# Wavee.Controls.Lyrics — synced lyrics rendering for WinUI

A WinUI 3 control library for displaying time-synchronized lyrics with shader effects, multi-language detection, and CJK romanization.

`net10.0-windows10.0.26100.0` · min platform `10.0.19041.0` · `UseWinUI=true` · x86 / x64 / ARM64.

## Tech stack

| Area                        | Library                                                                                |
|-----------------------------|----------------------------------------------------------------------------------------|
| 2D canvas + effects         | `Microsoft.Graphics.Win2D`                                                             |
| Compute shaders             | `ComputeSharp.D2D1.WinUI` (D2D1 pixel + compute shaders, AOT-friendly)                 |
| Cross-process textures      | `SpoutDx.Net.Interop.MultiPlatform` (DirectX texture sharing for video / live sources) |
| Audio I/O (preview)         | `NAudio.Wasapi`                                                                        |
| DirectX interop             | `Vortice.Direct3D11`                                                                   |
| Win32 P/Invoke              | `Vanara.PInvoke.User32`                                                                |
| **Language detection**      | `NTextCat` with `Wiki82.profile.xml` (~14.6 MB language model bundled as Content)      |
| Pinyin romanization         | `csharp-pinyin`                                                                        |
| Kana romanization           | `WanaKana-net`                                                                         |
| MVVM                        | `CommunityToolkit.Mvvm`                                                                |
| Lyrics search & parsing     | `Lyricify.Lyrics.Helper` (project ref; vendored multi-provider lyric fetcher)          |

## Folder map

```
Wavee.Controls.Lyrics/
├── Abstractions/           # Interfaces consumed by the host app
├── Assets/                 # AlbumArtPlaceholder.png, Wiki82.profile.xml (NTextCat language model)
├── ColorThief/             # Dominant-color / palette extraction from album art
├── Constants/
├── Controls/               # XAML controls (ImageSwitcher, NowPlayingCanvas, ShadowImage, …)
├── Converters/
├── Core/
├── Enums/
├── Extensions/
├── Helper/
├── Hooks/                  # Win32 hook helpers
├── Implementations/
├── Models/
├── Renderer/               # Lyric rendering pipeline (Win2D + shaders)
├── Services/
└── Shaders/                # ComputeSharp D2D shaders (HLSL-ish C#)
```

## Build quirk

The `EnsureIntermediateControlXaml` MSBuild target copies `Controls/ImageSwitcher.xaml`, `Controls/NowPlayingCanvas.xaml`, and `Controls/ShadowImage.xaml` into `IntermediateOutputPath/Controls/` before `CopyFilesToOutputDirectory`. This works around a WinUI loose-file XAML resolution quirk in cross-project consumption: without it, the host app's `ms-appx:///Wavee.Controls.Lyrics/Controls/*.xaml` lookups fail at runtime.

## Asset placement

`Assets/Wiki82.profile.xml` and `Assets/AlbumArtPlaceholder.png` are emitted as `Content` with `CopyToOutputDirectory=Always`. The host WinUI app's `RemoveDuplicateReferencedProjectAssets` build target deletes the duplicate copies that AppX packaging makes — see [Wavee.UI.WinUI/README.md](../Wavee.UI.WinUI/README.md) for why.

## Consumed by

- `Wavee.UI` (project ref → makes lyrics services available to non-WinUI consumers).
- `Wavee.UI.WinUI` (project ref → wires the controls into the now-playing UI).
