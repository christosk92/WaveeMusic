# Wavee.UI.WinUI — desktop client

The headline app: a WinUI 3 / Windows App SDK Spotify client.

`net10.0-windows10.0.26100.0` · min platform `10.0.26100.0` (Windows 11 24H2 — required for the on-device AI projection assemblies to load cleanly at startup) · **Single-project MSIX** · x86 / x64 / ARM64 · v0.1.0-beta.

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
| `VideoPlayerPage`     | Now-playing page — thin host for the same `ExpandedPlayerView` the popout window uses. |
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

Windows App SDK bundles a wide on-device ML surface by default. We use a narrow slice (Phi Silica via `Microsoft.Windows.AI.Text` for the on-device lyrics features), so the target keeps the bits Phi Silica needs and strips the rest:

**Kept** (required for Phi Silica on Copilot+ PCs):
- `Microsoft.Windows.AI.dll` — core projection
- `Microsoft.Windows.AI.Text.dll` — `LanguageModel`
- `Microsoft.Windows.AI.MachineLearning.dll` — NPU runtime backbone
- `onnxruntime.dll` (~21.7 MB native) — model execution
- `DirectML.dll` (~18.5 MB native) — NPU compute

**Stripped** (currently unused features; ~6–8 MB shaved):
- `Microsoft.Windows.AI.Imaging.Projection.dll` — Image Description / Super Resolution / object extractor + erase
- `Microsoft.Windows.AI.Generative.Projection.dll` — text→image generation
- `Microsoft.Windows.AI.ContentModeration.Projection.dll` — standalone safety APIs (Phi Silica's `ContentFilterOptions` ships in `Microsoft.Windows.AI.Text`)
- `Microsoft.ML.OnnxRuntime.dll` — managed ONNX wrapper (Phi Silica calls native onnxruntime.dll directly)

If a future feature needs Image Super Resolution or Image Description, remove the matching `<_StripManagedAi Include="…">` line.

## On-device AI (Copilot+ PC)

WaveeMusic uses **Phi Silica** — Microsoft's NPU-tuned 3.8B small language model that ships with the Windows App SDK — to power opt-in lyrics features everywhere lyrics are shown: explain a single lyric line, and explain the song's lyrics meaning. All inference runs on-device against the user's NPU; nothing is sent off the machine.

**Hardware requirement:** Copilot+ PC (Snapdragon X Elite/Plus, Intel Core Ultra Series 2, AMD Ryzen AI 300+) running Windows 11 24H2. On every other configuration the affordances are hidden and the model is never downloaded. The app's `<TargetPlatformMinVersion>10.0.17763.0</>` (Windows 10 1809) means most users won't see the feature — that's intentional.

**Opt-in by default:** Settings → On-device AI exposes a master toggle (default OFF) plus per-feature toggles. Until the user flips the master toggle, no AI affordance renders, no Phi Silica model is downloaded, and zero calls land in `Microsoft.Windows.AI.Text`.

**Region gating:** Phi Silica isn't available in China. The Settings UI surfaces "Not available in your region" and the affordances stay hidden.

**Limited Access Feature:** Phi Silica is a [Limited Access Feature](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.limitedaccessfeatures). Production builds need an LAF unlock token (request via https://go.microsoft.com/fwlink/?linkid=2271232) baked into `Package.appxmanifest`. Without it, `LanguageModel.CreateAsync()` throws — `AiCapabilities` swallows that and the affordances stay hidden.

**Setup follows Microsoft's official "Get started building an app with Windows AI APIs" guide ([learn.microsoft.com](https://learn.microsoft.com/en-us/windows/ai/apis/get-started)) plus a 2.0.1 deployment workaround.**

Two layers are needed:

### Layer 1 — OS access gate (Microsoft documented setup)

Without this, the Windows AI runtime refuses to load the projection assemblies for our app even if we deploy them ourselves:

- `<systemai:Capability Name="systemAIModels" />` in `Package.appxmanifest` (under a new `xmlns:systemai="http://schemas.microsoft.com/appx/manifest/systemai/windows10"` namespace).
- `MaxVersionTested="10.0.26226.0"` on `<TargetDeviceFamily>` entries.
- `<AppxOSMinVersionReplaceManifestVersion>false</>` + `<AppxOSMaxVersionTestedReplaceManifestVersion>false</>` in the csproj so VS doesn't rewrite the manifest at packaging time.

### Layer 2 - Force AI projection assemblies into AppX (2.0.1 workaround)

The csproj also contains two MSBuild workaround targets:

- `IncludeWindowsAiProjectionAssembliesInMsixPayload` adds the already-resolved AI projection assemblies from `$(OutputPath)` to `@(AppxPackagePayload)` for package generation.
- `CopyWindowsAiProjectionAssembliesToAppxLayout` and `CopyWindowsAiProjectionAssembliesToAppxLayoutAfterPri` copy those same assemblies into `$(OutputPath)\AppX\` after the known layout-producing targets. That is the folder used by packaged F5/debug deploy.

Why this layer is needed: WinAppSDK 2.0.1 has a known regression where the AI managed projection assemblies don't deploy into AppX even though they're present in the NuGet package's `lib/` folder. The 2.0.1 release notes explicitly acknowledge AI regressions ("*AICapabilities is missing from 2.0.1 ... We plan to restore them in the May release*"). Microsoft's official AI setup guide actually targets WinAppSDK **1.8 experimental**, which doesn't have this regression.

What we tried before settling on this:

- `<WindowsAppSDKSelfContained>true</>` — bundles native AI DLLs only, leaves managed projections out.
- `<WindowsAppSDKFrameworkPackageReference>false</>` — bundles every other `Microsoft.Windows.*.Projection.dll` into AppX EXCEPT the `Microsoft.Windows.AI.*` set (specifically filtered).
- Verified the installed `Microsoft.WindowsAppRuntime.2 v2.0.1.0` framework MSIX at `C:\Program Files\WindowsApps\…` ships the native AI DLLs but **zero** `.Projection.dll` files. The framework path Microsoft assumes works on 1.8 simply has nothing to load on 2.0.1.

The csproj also contains `PreserveWindowsAiManifestMaxVersionTested`, which patches the generated AppX manifest back to `MaxVersionTested="10.0.26226.0"` immediately before the MSIX recipe/package is produced. This is needed because the local MSIX tooling rewrites the `Windows.Desktop` entry back to the installed Windows SDK version (`10.0.26100.0`) even though the source manifest is correct.

**Delete these MSBuild workaround targets when you upgrade to a WinAppSDK/MSIX tooling release that fixes AI deployment and manifest preservation.** The systemAIModels capability + MaxVersionTested are sufficient on a working SDK; these targets are only for the 2.0.1/tooling gap.

**Code map:**

| File | Role |
|---|---|
| `Services/AiCapabilities.cs` | Composite gate — hardware (`LanguageModel.GetReadyState()`) + region + user opt-in. The single decision point every AI affordance binds against. |
| `Services/LyricsAiService.cs` | Wraps `LanguageModel.GenerateResponseAsync` for line explanation and lyrics meaning. In-memory cache is keyed by `(trackUri, lineIndex)` for lines and by `trackUri` for lyrics meaning; per-track meaning uses a shared in-flight task so multiple UI surfaces do not duplicate model calls. |
| `ViewModels/LyricsAiPanelViewModel.cs` | UI orchestration: cancellation-aware commands, observable result/caption/busy state. Resolves the currently synced line via `LyricsViewModel.LastServicePosition` so "Explain current line" works without canvas hit-testing. |
| `Controls/SidebarPlayer/LyricsAiPanel.xaml(.cs)` | The floating affordance row + result chrome that mounts above the lyrics column on the expanded now-playing view. |
| `Controls/AiSparkleIcon.xaml(.cs)` | The animated sparkle icon. State-driven Composition animations (pulse / rotate / wiggle) replace a Lottie source — same on-screen behavior, no LottieGen step. |
| `Themes/AiBrandTheme.xaml` | `AiAccentGradientBrush` (Copilot rainbow), `AiAccentSolidBrush`, `AiBorderBrush`, `AiPanelBackgroundBrush`. Light/dark/HighContrast variants. `SparkleAffordanceButtonStyle` and `AiCaptionTextBlockStyle` for visual consistency. |
| `Controls/Settings/AiSettingsSection.xaml(.cs)` + `ViewModels/AiSettingsViewModel.cs` | The Settings → On-device AI section. Master + per-feature toggles. |

**Visual language** mirrors Windows 11's own AI surfaces (Settings → System → AI components): the FluentIcons `Sparkle` glyph, the Copilot rainbow gradient on hover/active, the AI-prefixed caption underline. Falls back gracefully when "Reduce animations" is on (Composition animations skip; static glyph stays).

**Model download UX** uses two surfaces in parallel — the in-Settings ProgressBar (visible while you're on the AI section) and a Windows toast posted via `Microsoft.Windows.AppNotifications` (visible from the Action Center even after you navigate away). Both update from the same `IProgress<double>` flowing out of `AiCapabilities.EnsureLanguageModelReadyAsync`. The toast follows the same lifecycle the Microsoft Store uses for app installs: a single notification with an in-place updatable progress bar that swaps to a "Try it" button when done. Activation is wired through `AppNotificationActivationRouter` (deep-linking to Settings, now-playing, retry, or background cancel) — see `App.xaml.cs.OnAppNotificationInvoked` and the toast manifest extensions in `Package.appxmanifest`.

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
