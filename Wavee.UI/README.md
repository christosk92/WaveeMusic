# Wavee.UI — framework-neutral UI service layer

Plain C# class library that sits between the core protocol library (`Wavee`) and the WinUI app (`Wavee.UI.WinUI`). No XAML, no WinUI dependency — just contracts and services.

`net10.0-windows10.0.26100.0` (uses some Windows-platform-specific contracts), no `UseWinUI`, no MSIX. Targets `AnyCPU;x86;x64;ARM64`.

## Why a separate project

Two reasons:

1. **Testability**. `Wavee.UI.Tests` exercises this layer directly without booting WinUI. Anything that can be tested in plain xUnit lives here.
2. **Separation of concerns**. The WinUI project stays focused on view-side concerns (XAML, ViewModels, page navigation). This project owns the UI-shaped contracts and service implementations that don't depend on a particular UI framework.

If a future `Wavee.UI.Avalonia` ever appeared, this project would stay unchanged.

## Folder map

```
Wavee.UI/
├── Contracts/      # IPlaybackService, IPlaybackStateService, …
├── Enums/          # RepeatMode, …
├── Models/         # ArtistCredit, QueueItem, PlaybackContextInfo, PlaybackResult, PlaybackErrorEvent, PlayContextOptions, …
├── Services/       # CardPreviewPlaybackCoordinator, IPreviewAudioPlaybackEngine, LyricsProviderSelector, TrackColorHintService
└── Threading/      # IUiDispatcher (abstraction over the WinUI dispatcher; mockable in tests)
```

## InternalsVisibleTo

```xml
<InternalsVisibleTo Include="Wavee.UI.Tests" />
<InternalsVisibleTo Include="Wavee.UI.WinUI" />
```

`internal` symbols here are part of the contract with the WinUI app and the test project — not loose ends. If you make something `internal` here intending it for the WinUI app, you don't have to make it `public` to consume it.

## Dependencies

`Microsoft.Extensions.Logging.Abstractions` only. Project refs: `Wavee`, `Wavee.Controls.Lyrics`.
