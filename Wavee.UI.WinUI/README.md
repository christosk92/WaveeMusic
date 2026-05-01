# Wavee.UI.WinUI — desktop client

The headline app: a WinUI 3 / Windows App SDK Spotify client.

`net10.0-windows10.0.26100.0` · min platform `10.0.17763.0` (Windows 10 1809) · **Single-project MSIX** · x86 / x64 / ARM64 · v0.1.0-beta.

## Composition

```
App.OnLaunched
  → AppLifecycleHelper.ConfigureHost()      // builds IHost (Microsoft.Extensions.Hosting)
  → Ioc.Default.ConfigureServices(...)      // wires CommunityToolkit MVVM Ioc to the same container
  → Force-resolve IMetadataDatabase         // runs schema migrations before first paint
  → AppModel = Ioc.Default.GetRequiredService<AppModel>()
  → Start CacheCleanupService
  → MainWindow.Instance.Activate()          // user sees the window
  → MainWindow.Instance.InitializeApplicationAsync()  // deferred init (login state, library sync, etc.)
```

The Wavee `Session` (from the core library) is registered as a singleton wired up with the configured `SessionConfig` (device id / locale), the `IHttpClientFactory`, an `ILogger<Session>`, and an optional `IRemoteStateRecorder` (off by default — turn it on from the Debug page).

Crash handling is wired at three levels — XAML, AppDomain, and the unobserved-task scheduler — all funnel through `LogUnhandledException`, which best-effort writes a redacted log via `PiiRedactor` to `AppPaths.CrashLogPath`.

## Folder map

```
Wavee.UI.WinUI/
├── App.xaml.cs                # Composition root + global crash handlers
├── MainWindow.xaml.cs         # Shell window, deferred init
├── Assets/                    # Logos, splash, fonts (MediaPlayerIcons.ttf)
├── Behaviors/                 # XAML attached behaviors (track/header drag, keyboard nav, etc.)
├── Collections/               # Custom IObservableCollection helpers
├── Controls/                  # 50+ reusables — see "Notable controls" below
├── Converters/                # XAML value converters
├── Data/
│   ├── Contexts/              # PlaybackStateService, LibraryDataService, AuthStateService, …
│   ├── Stores/                # ArtistStore, AlbumStore, PlaylistStore, ExtendedMetadataStore (hot caches)
│   ├── Messages/              # WeakReferenceMessenger event types
│   └── Models/                # AppModel, app-wide DTOs
├── Diagnostics/               # In-app debug panels backing the DebugPage
├── DragDrop/                  # Drag-and-drop sources/targets for library items
├── Extensions/                # Misc extension methods
├── Helpers/                   # AppLifecycleHelper, AppPaths, PiiRedactor, AppLocalization, NavigationHelper, …
├── Properties/                # Resx
├── Services/                  # See "Notable services" below
├── Strings/                   # Localized resources
├── Styles/                    # Theme dictionaries (FontResources, ThemeDictionaries, *Styles.xaml)
├── Themes/                    # Theme definitions
├── ViewModels/                # MVVM view models
├── Views/                     # Pages (HomePage, ArtistPage, …) — see "Pages"
└── Windows/                   # Secondary windows (e.g. blocking error window for migration failures)
```

## Pages

| Page                  | What                                                                                |
|-----------------------|-------------------------------------------------------------------------------------|
| `HomePage`            | Spotify home feed (curated playlists, new releases, personalized shelves).          |
| `SearchPage`          | Top results, tracks, artists, albums, playlists; quick search via Omnibar control.  |
| `ArtistPage`          | Discography, top tracks, biography, related artists, color extraction.              |
| `AlbumPage`           | Tracklist, metadata, related releases.                                              |
| `PlaylistPage`        | Tracklist with inline-editable name/description, owner info, collaborator chips.    |
| `LikedSongsView`      | Saved tracks library.                                                               |
| `ArtistsLibraryView`  | Saved artists.                                                                      |
| `AlbumsLibraryView`   | Saved albums.                                                                       |
| `LocalLibraryPage`    | Local audio + video files indexed from disk by `LocalIndexerHostedService`.         |
| `VideoPlayerPage`     | Full-screen video — Spotify videos via PlayReady DRM, plus local files.             |
| `ProfilePage`         | User profile, friends, listening stats.                                             |
| `SettingsPage`        | Theme, audio device, EQ, playback, diagnostics, storage, language.                  |
| `ConcertPage`         | Concert / live event info via Pathfinder.                                           |
| `CreatePlaylistPage`  | Playlist creation flow.                                                             |
| `FeedbackPage`        | User feedback submission (`Wavee/Core/Feedback/`).                                  |
| `DebugPage`           | Internal diagnostics — IPC health, memory, remote state recorder log.               |
| `ShellPage`           | Main shell (sidebar + tabs + now-playing bar + right panel).                        |

## Notable controls

All under `Controls/`:

- **Player** — `PlayerBar`, `SidebarPlayer`, `ExpandedNowPlayingLayout`, `OutputDevicePicker`, `EqualizerCurveControl`, `MiniVideoPlayer`.
- **Tracks** — `TrackItem`, `TrackDataGrid` (virtualized, sortable), `TrackBehavior`, `HeartButton`, `FollowButton`.
- **Library / cards** — `ContentCard`, `LibraryGridView`, `ExpandableAlbumGrid`, `SectionShelf`, `HeroHeader`, `CrossFadeImage`, `PlaylistMosaicService`.
- **Navigation / search** — `TabBar`, `Omnibar`, `Sidebar`, `QueueTabView`, `RightPanelView`.
- **Connect** — `SpotifyConnectDialog` (device picker), `ActivityBell`.
- **Editing** — `InlineEditableText` (click-to-edit titles / descriptions).
- **Settings** — modular `Settings*Section` controls.
- **Misc** — `JsonRichTextBlock`, `LocationButton`, `PreviewAudioVisualizer`, `ShimmerListView`.

## Notable services

Under `Services/` and `Data/Contexts/`:

| Service                            | Role                                                                                       |
|------------------------------------|--------------------------------------------------------------------------------------------|
| `PlaybackStateService`             | Bridges Spotify Connect cluster state to UI as observable properties.                      |
| `PlaybackService`                  | Fire-and-forget command executor (Play / Pause / Seek / Next / Prev) over Connect.         |
| `LibraryDataService`               | Reads liked tracks/albums/artists from SQLite + playlist cache; reacts to sync messages.   |
| `LibrarySyncOrchestrator`          | Initial + delta sync of Spotify library to local SQLite.                                   |
| `LocalIndexerHostedService`*       | Background indexer for local audio/video files (lives in `Wavee/Core/Library/Local/`, registered as a hosted service from this project). |
| `SpotifyVideoProvider`             | Spotify music-video playback: DASH manifest synthesis, PlayReady license, MediaPlayer.     |
| `LocalMediaPlayer`                 | Local-file video playback engine (complement to SpotifyVideoProvider).                     |
| `ActiveVideoSurfaceService`        | Arbitrates between Spotify and local video surfaces.                                       |
| `WindowsVideoThumbnailExtractor`   | Generates thumbnails for local video files.                                                |
| `RemoteStateRecorder`              | Implements `IRemoteStateRecorder` from the core lib; capped at 500 entries; backs DebugPage.|
| `LyricsService`                    | Multi-provider lyrics search & parsing (via `Lyricify.Lyrics.Helper`).                     |
| `MusicVideoDiscoveryService`       | Background discovery of music videos linked to audio tracks; raises availability messages. |
| `MusicVideoMetadataService`        | Music-video catalog cache and metadata.                                                    |
| `ProfileFetcher` / `UserProfileResolver` | Profile fetching and caching.                                                        |
| `TrackMetadataEnricher`            | Extended metadata (credits, features) for rich detail pages.                               |
| `HomeResponseParserFactory` (V1/V2)| Parses Spotify home feed JSON into card data structures.                                   |
| `HomeFeedCache`                    | In-session cache to suppress redundant home-feed lookups.                                  |
| `ImageCacheService`                | In-memory LRU cache for downloaded images.                                                 |
| `CacheCleanupService`              | Periodic image-cache eviction.                                                             |
| `MemoryBudgetService`              | Memory monitoring and GC tuning.                                                           |
| `PreviewAudioGraphService`         | Card-preview WAV graph (separate from main audio engine).                                  |
| `ThemeService` / `ThemeColorService`| Theme switching and color extraction from album art.                                      |
| `SettingsService`                  | App configuration (theme, audio device, language, cache profile, …).                       |
| `NotificationService`              | Toast notifications.                                                                       |
| `UpdateService`                    | App update check.                                                                          |
| `UiOperationProfiler`              | Timing-instrumented operation profiling for UI debugging.                                  |
| `RecentlyPlayedService`            | Recently-played items (Spotify side).                                                      |
| `ActivityService`                  | App-wide event bus for activity events (play / pause / like).                              |
| `FriendsFeedService`               | Friends' recent listens.                                                                   |

## Custom build targets

`Wavee.UI.WinUI.csproj` includes three custom MSBuild targets — each one exists for a specific reason:

### `BuildAudioHost` (BeforeTargets: PrepareForBuild;ResolveProjectReferences;CoreCompile;Build)

Spawns an isolated `dotnet build` subprocess for `Wavee.AudioHost` with `Platform=x64`. Why a subprocess instead of a project reference: the parent build is ARM64 (or whatever you're targeting), and an in-process MSBuild reference would inherit the parent's project-evaluation cache. That cache poisoning could land an ARM64 NVorbis.dll in AudioHost's bin and crash the audio process at startup. A fresh `dotnet build` evaluates the project graph from scratch with `Platform=x64`, so every transitive reference is x64 too. Cost: a few seconds per WinUI launch. Benefit: structural elimination of the stale-neighbour-DLL bug class.

### `RemoveDuplicateReferencedProjectAssets` (AfterTargets: CopyFilesToOutputDirectory;_GenerateProjectPriFileCore)

WinUI AppX packaging copies `<Content>` items from referenced projects twice — once flattened to the AppX root (which is what the runtime reads via `AppContext.BaseDirectory`) and once preserved under the per-project subfolder. Removes the orphan duplicates (~14.6 MB for `Wiki82.profile.xml` alone). Carefully scoped to specific files, **not** the entire `Wavee.Controls.Lyrics/` folder, because that folder also contains compiled per-assembly XAML (`.xbf` for `NowPlayingCanvas`, `ImageSwitcher`, `ShadowImage`) which WinUI resolves at runtime via `ms-appx:///Wavee.Controls.Lyrics/Controls/*.xaml`.

### `StripUnusedWindowsAiPayload` (AfterTargets: CopyFilesToOutputDirectory;_GenerateProjectPriFileCore)

Windows App SDK bundles on-device ML by default — `Microsoft.Windows.AI.*` projections plus `onnxruntime.dll` (21.7 MB), `DirectML.dll` (18.5 MB), `Microsoft.Windows.AI.MachineLearning.dll` (1.0 MB). The app uses zero of these APIs (verified by grep), so they're ~42 MB on disk and 30-60 MB process RSS for nothing. The target deletes them under both `$(OutputPath)` and `AppX/` for every RID. Delete this target if a future feature ever needs on-device ML.

## GC / publish settings

- **Workstation GC** (`<ServerGarbageCollection>false</>`) with **concurrent** enabled. Server GC creates one heap per logical processor and grows working set aggressively — the wrong trade for image-heavy navigation UI. The audio pipeline runs out-of-process, so we don't need server GC's footprint to protect playback latency.
- **`<PublishReadyToRun>false</>`** — both ReadyToRun and trimming require `<SelfContained>true</>`, and going self-contained adds ~70-100 MB to the AppX. Validating trim safety across WinUI / ReactiveUI / MVVM Toolkit / ComputeSharp would be a separate project. Re-enable both together if/when self-contained packaging is revisited.

## Project relationships

```
Wavee.UI.WinUI
    ├── Wavee                    — core protocol (Session, Connect, audio orchestrator)
    ├── Wavee.UI                 — framework-neutral UI service layer (testable without WinUI)
    ├── Wavee.Playback.Contracts — IPC DTOs (project ref here, source-included into AudioHost)
    ├── Wavee.Controls.Lyrics    — lyrics rendering control library
    └── Lyricify.Lyrics.Helper   — multi-provider lyrics search (vendored)

…and at runtime, talks over a named pipe to:
    Wavee.AudioHost              — out-of-process audio runtime (built by BuildAudioHost target)
```

## Run

```bash
dotnet run --project Wavee.UI.WinUI
```

VS / Rider F5 also work. The first run goes through the OAuth flow and writes a DPAPI-encrypted credentials blob; subsequent launches reuse it.
