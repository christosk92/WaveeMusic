const fs = require('fs');

const nodes = [];
const edges = [];

// 1. TitleFontSizer.cs
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/Helpers/TitleFontSizer.cs',
  type: 'file',
  name: 'TitleFontSizer.cs',
  filePath: 'src/Wavee.UI.WinUI/Helpers/TitleFontSizer.cs',
  summary: 'Provides responsive font-size and line-height calculations for hero, compact-hero, and strip title strings based on text length.',
  tags: ['utility', 'ui-helper', 'typography'],
  complexity: 'simple'
});
nodes.push({
  id: 'class:src/Wavee.UI.WinUI/Helpers/TitleFontSizer.cs:TitleFontSizer',
  type: 'class',
  name: 'TitleFontSizer',
  filePath: 'src/Wavee.UI.WinUI/Helpers/TitleFontSizer.cs',
  lineRange: [17, 66],
  summary: 'Static helper computing dynamic font size and line height for various title display modes (Hero, Strip, CompactHero) based on character count.',
  tags: ['utility', 'typography', 'ui-helper'],
  complexity: 'simple'
});
edges.push({ source: 'file:src/Wavee.UI.WinUI/Helpers/TitleFontSizer.cs', target: 'class:src/Wavee.UI.WinUI/Helpers/TitleFontSizer.cs:TitleFontSizer', type: 'contains', direction: 'forward', weight: 1.0 });
edges.push({ source: 'file:src/Wavee.UI.WinUI/Helpers/TitleFontSizer.cs', target: 'class:src/Wavee.UI.WinUI/Helpers/TitleFontSizer.cs:TitleFontSizer', type: 'exports', direction: 'forward', weight: 0.8 });

// 2. AppSystemBackdrop.cs
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/Helpers/UI/AppSystemBackdrop.cs',
  type: 'file',
  name: 'AppSystemBackdrop.cs',
  filePath: 'src/Wavee.UI.WinUI/Helpers/UI/AppSystemBackdrop.cs',
  summary: 'Custom SystemBackdrop subclass that wires a MicaController to the target window on connection and tears it down on disconnection.',
  tags: ['ui-helper', 'mica', 'backdrop', 'winui'],
  complexity: 'simple'
});
nodes.push({
  id: 'class:src/Wavee.UI.WinUI/Helpers/UI/AppSystemBackdrop.cs:AppSystemBackdrop',
  type: 'class',
  name: 'AppSystemBackdrop',
  filePath: 'src/Wavee.UI.WinUI/Helpers/UI/AppSystemBackdrop.cs',
  lineRange: [8, 36],
  summary: 'Manages Mica backdrop lifecycle: initialises MicaController on target connect and disposes it on disconnect.',
  tags: ['ui-helper', 'backdrop', 'mica'],
  complexity: 'simple'
});
edges.push({ source: 'file:src/Wavee.UI.WinUI/Helpers/UI/AppSystemBackdrop.cs', target: 'class:src/Wavee.UI.WinUI/Helpers/UI/AppSystemBackdrop.cs:AppSystemBackdrop', type: 'contains', direction: 'forward', weight: 1.0 });
edges.push({ source: 'file:src/Wavee.UI.WinUI/Helpers/UI/AppSystemBackdrop.cs', target: 'class:src/Wavee.UI.WinUI/Helpers/UI/AppSystemBackdrop.cs:AppSystemBackdrop', type: 'exports', direction: 'forward', weight: 0.8 });

// 3. FrameworkElementExtensions.cs
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/Helpers/UI/FrameworkElementExtensions.cs',
  type: 'file',
  name: 'FrameworkElementExtensions.cs',
  filePath: 'src/Wavee.UI.WinUI/Helpers/UI/FrameworkElementExtensions.cs',
  summary: 'Extension methods for FrameworkElement; provides ChangeCursor to set the pointer cursor via reflection on UIElement.',
  tags: ['utility', 'extension-methods', 'ui-helper'],
  complexity: 'simple',
  languageNotes: 'Uses reflection to set the ProtectedCursor property on UIElement, a common WinUI workaround for cursor override.'
});

// 4. ScrollViewExtensions.cs
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/Helpers/UI/ScrollViewExtensions.cs',
  type: 'file',
  name: 'ScrollViewExtensions.cs',
  filePath: 'src/Wavee.UI.WinUI/Helpers/UI/ScrollViewExtensions.cs',
  summary: 'Extension method for WinUI ScrollView that adds immediate (zero-animation) scroll to a given offset.',
  tags: ['utility', 'extension-methods', 'scroll'],
  complexity: 'simple'
});

// 5. ThemeHelper.cs
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/Helpers/UI/ThemeHelper.cs',
  type: 'file',
  name: 'ThemeHelper.cs',
  filePath: 'src/Wavee.UI.WinUI/Helpers/UI/ThemeHelper.cs',
  summary: 'Static utility for resolving the actual WinUI theme of a FrameworkElement, setting the root theme, and toggling between Light and Dark.',
  tags: ['utility', 'theme', 'ui-helper'],
  complexity: 'simple'
});

// 6. MainWindow.xaml
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/MainWindow.xaml',
  type: 'file',
  name: 'MainWindow.xaml',
  filePath: 'src/Wavee.UI.WinUI/MainWindow.xaml',
  summary: 'XAML definition for the application main window shell, declaring root layout and hosting the navigation frame.',
  tags: ['markup', 'shell', 'winui'],
  complexity: 'simple'
});

// 7. MainWindow.xaml.cs
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/MainWindow.xaml.cs',
  type: 'file',
  name: 'MainWindow.xaml.cs',
  filePath: 'src/Wavee.UI.WinUI/MainWindow.xaml.cs',
  summary: 'Code-behind for the main application window: handles startup initialisation, presenter (fullscreen/windowed) swap, graceful shutdown with session persistence, and on-minimize/off-screen memory-release scheduling.',
  tags: ['entry-point', 'shell', 'window-lifecycle', 'memory-management'],
  complexity: 'complex'
});
nodes.push({
  id: 'class:src/Wavee.UI.WinUI/MainWindow.xaml.cs:MainWindow',
  type: 'class',
  name: 'MainWindow',
  filePath: 'src/Wavee.UI.WinUI/MainWindow.xaml.cs',
  lineRange: [23, 470],
  summary: 'WinUI main window: bootstraps app services on first launch, manages fullscreen/windowed presenter transitions, schedules background memory release when minimized, and orchestrates graceful teardown with tab-session persistence on close.',
  tags: ['shell', 'window-lifecycle', 'entry-point', 'memory-management'],
  complexity: 'complex'
});
edges.push({ source: 'file:src/Wavee.UI.WinUI/MainWindow.xaml.cs', target: 'class:src/Wavee.UI.WinUI/MainWindow.xaml.cs:MainWindow', type: 'contains', direction: 'forward', weight: 1.0 });
edges.push({ source: 'file:src/Wavee.UI.WinUI/MainWindow.xaml.cs', target: 'class:src/Wavee.UI.WinUI/MainWindow.xaml.cs:MainWindow', type: 'exports', direction: 'forward', weight: 0.8 });

// 8. PodcastBrowseSection.cs
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/Models/PodcastBrowse/PodcastBrowseSection.cs',
  type: 'file',
  name: 'PodcastBrowseSection.cs',
  filePath: 'src/Wavee.UI.WinUI/Models/PodcastBrowse/PodcastBrowseSection.cs',
  summary: 'Data model definitions for the Podcast Browse page: section (with pagination), tile, category chip/group, and breadcrumb item view-model records.',
  tags: ['data-model', 'podcast', 'browse'],
  complexity: 'moderate'
});
nodes.push({
  id: 'class:src/Wavee.UI.WinUI/Models/PodcastBrowse/PodcastBrowseSection.cs:PodcastBrowseSection',
  type: 'class',
  name: 'PodcastBrowseSection',
  filePath: 'src/Wavee.UI.WinUI/Models/PodcastBrowse/PodcastBrowseSection.cs',
  lineRange: [42, 86],
  summary: 'Observable view-model section for a podcast browse page shelf, carrying title, items collection, pagination state, and shimmer slot count.',
  tags: ['data-model', 'podcast', 'pagination'],
  complexity: 'moderate'
});
edges.push({ source: 'file:src/Wavee.UI.WinUI/Models/PodcastBrowse/PodcastBrowseSection.cs', target: 'class:src/Wavee.UI.WinUI/Models/PodcastBrowse/PodcastBrowseSection.cs:PodcastBrowseSection', type: 'contains', direction: 'forward', weight: 1.0 });
edges.push({ source: 'file:src/Wavee.UI.WinUI/Models/PodcastBrowse/PodcastBrowseSection.cs', target: 'class:src/Wavee.UI.WinUI/Models/PodcastBrowse/PodcastBrowseSection.cs:PodcastBrowseSection', type: 'exports', direction: 'forward', weight: 0.8 });

// 9. Package.appxmanifest
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/Package.appxmanifest',
  type: 'file',
  name: 'Package.appxmanifest',
  filePath: 'src/Wavee.UI.WinUI/Package.appxmanifest',
  summary: 'MSIX application manifest declaring package identity, capabilities (internet, microphone, LAF tokens), and supported file-type activations for WaveeMusic.',
  tags: ['configuration', 'msix', 'packaging'],
  complexity: 'moderate'
});

// 10. launchSettings.json
nodes.push({
  id: 'config:src/Wavee.UI.WinUI/Properties/launchSettings.json',
  type: 'config',
  name: 'launchSettings.json',
  filePath: 'src/Wavee.UI.WinUI/Properties/launchSettings.json',
  summary: 'Visual Studio launch profiles for the WinUI app, specifying the MsixPackage launch command for F5 runs from the IDE.',
  tags: ['configuration', 'launch-settings', 'development'],
  complexity: 'simple'
});

// 11. README.md
nodes.push({
  id: 'document:src/Wavee.UI.WinUI/README.md',
  type: 'document',
  name: 'README.md',
  filePath: 'src/Wavee.UI.WinUI/README.md',
  summary: 'Comprehensive reference for the Wavee.UI.WinUI desktop project: folder map, page catalogue, notable controls/services, custom MSBuild targets, on-device AI setup, GC configuration, and project relationships.',
  tags: ['documentation', 'architecture', 'overview'],
  complexity: 'complex'
});
edges.push({ source: 'document:src/Wavee.UI.WinUI/README.md', target: 'file:src/Wavee.UI.WinUI/MainWindow.xaml.cs', type: 'documents', direction: 'forward', weight: 0.5 });

// 12. ActiveVideoSurfaceService.cs
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/Services/ActiveVideoSurfaceService.cs',
  type: 'file',
  name: 'ActiveVideoSurfaceService.cs',
  filePath: 'src/Wavee.UI.WinUI/Services/ActiveVideoSurfaceService.cs',
  summary: 'Singleton service that arbitrates ownership of the single active video surface among multiple consumers, forwarding surface-change events from providers to the current owner on the UI thread.',
  tags: ['service', 'video', 'surface-management', 'singleton'],
  complexity: 'complex'
});
nodes.push({
  id: 'class:src/Wavee.UI.WinUI/Services/ActiveVideoSurfaceService.cs:ActiveVideoSurfaceService',
  type: 'class',
  name: 'ActiveVideoSurfaceService',
  filePath: 'src/Wavee.UI.WinUI/Services/ActiveVideoSurfaceService.cs',
  lineRange: [11, 213],
  summary: 'Manages exclusive ownership of the active video playback surface: registers providers, handles acquire/release by consumers, and fans out surface-change notifications to the current owner on the dispatcher queue.',
  tags: ['service', 'video', 'surface-management'],
  complexity: 'complex'
});
edges.push({ source: 'file:src/Wavee.UI.WinUI/Services/ActiveVideoSurfaceService.cs', target: 'class:src/Wavee.UI.WinUI/Services/ActiveVideoSurfaceService.cs:ActiveVideoSurfaceService', type: 'contains', direction: 'forward', weight: 1.0 });
edges.push({ source: 'file:src/Wavee.UI.WinUI/Services/ActiveVideoSurfaceService.cs', target: 'class:src/Wavee.UI.WinUI/Services/ActiveVideoSurfaceService.cs:ActiveVideoSurfaceService', type: 'exports', direction: 'forward', weight: 0.8 });

// 13. LibraryDataServiceAddToPlaylistSubmitter.cs
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/Services/AddToPlaylist/LibraryDataServiceAddToPlaylistSubmitter.cs',
  type: 'file',
  name: 'LibraryDataServiceAddToPlaylistSubmitter.cs',
  filePath: 'src/Wavee.UI.WinUI/Services/AddToPlaylist/LibraryDataServiceAddToPlaylistSubmitter.cs',
  summary: 'Thin adapter that implements the add-to-playlist submission contract by delegating track additions to the playlist mutation service.',
  tags: ['service', 'adapter', 'playlist'],
  complexity: 'simple'
});

// 14. AiCapabilities.cs
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/Services/AiCapabilities.cs',
  type: 'file',
  name: 'AiCapabilities.cs',
  filePath: 'src/Wavee.UI.WinUI/Services/AiCapabilities.cs',
  summary: 'Single decision point for on-device AI availability: composes hardware detection (Phi Silica), region check, user opt-in, and LAF unlock to produce IsAiAvailableAndEnabled and feature-specific flags used by every AI affordance.',
  tags: ['service', 'ai', 'capability-gate', 'phi-silica'],
  complexity: 'complex'
});
nodes.push({
  id: 'class:src/Wavee.UI.WinUI/Services/AiCapabilities.cs:AiCapabilities',
  type: 'class',
  name: 'AiCapabilities',
  filePath: 'src/Wavee.UI.WinUI/Services/AiCapabilities.cs',
  lineRange: [38, 464],
  summary: 'Gatekeeper for Phi Silica on-device AI: checks hardware readiness, region allow-list, user opt-in, and LAF unlock status; exposes EnsureLanguageModelReadyAsync and diagnostic description helpers.',
  tags: ['service', 'ai', 'capability-gate', 'phi-silica'],
  complexity: 'complex'
});
edges.push({ source: 'file:src/Wavee.UI.WinUI/Services/AiCapabilities.cs', target: 'class:src/Wavee.UI.WinUI/Services/AiCapabilities.cs:AiCapabilities', type: 'contains', direction: 'forward', weight: 1.0 });
edges.push({ source: 'file:src/Wavee.UI.WinUI/Services/AiCapabilities.cs', target: 'class:src/Wavee.UI.WinUI/Services/AiCapabilities.cs:AiCapabilities', type: 'exports', direction: 'forward', weight: 0.8 });

// 15. AiNotificationService.cs
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/Services/AiNotificationService.cs',
  type: 'file',
  name: 'AiNotificationService.cs',
  filePath: 'src/Wavee.UI.WinUI/Services/AiNotificationService.cs',
  summary: 'Manages Windows toast notifications for the on-device AI model lifecycle: preparing (with download progress bar), ready, and error states with action buttons.',
  tags: ['service', 'notifications', 'ai', 'toast'],
  complexity: 'complex'
});
nodes.push({
  id: 'class:src/Wavee.UI.WinUI/Services/AiNotificationService.cs:AiNotificationService',
  type: 'class',
  name: 'AiNotificationService',
  filePath: 'src/Wavee.UI.WinUI/Services/AiNotificationService.cs',
  lineRange: [27, 203],
  summary: 'Sends and updates toast notifications through the Windows AppNotificationManager to track Phi Silica model download progress, readiness, and errors.',
  tags: ['service', 'notifications', 'ai'],
  complexity: 'complex'
});
edges.push({ source: 'file:src/Wavee.UI.WinUI/Services/AiNotificationService.cs', target: 'class:src/Wavee.UI.WinUI/Services/AiNotificationService.cs:AiNotificationService', type: 'contains', direction: 'forward', weight: 1.0 });
edges.push({ source: 'file:src/Wavee.UI.WinUI/Services/AiNotificationService.cs', target: 'class:src/Wavee.UI.WinUI/Services/AiNotificationService.cs:AiNotificationService', type: 'exports', direction: 'forward', weight: 0.8 });

// 16. AlbumPrefetcher.cs
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/Services/AlbumPrefetcher.cs',
  type: 'file',
  name: 'AlbumPrefetcher.cs',
  filePath: 'src/Wavee.UI.WinUI/Services/AlbumPrefetcher.cs',
  summary: 'Background album metadata pre-fetcher: batches album URI requests with a debounce delay, fetches protobuf metadata, and broadcasts partial album results via messenger for Home shelf pre-population.',
  tags: ['service', 'prefetch', 'album', 'metadata'],
  complexity: 'complex'
});
nodes.push({
  id: 'class:src/Wavee.UI.WinUI/Services/AlbumPrefetcher.cs:AlbumPrefetcher',
  type: 'class',
  name: 'AlbumPrefetcher',
  filePath: 'src/Wavee.UI.WinUI/Services/AlbumPrefetcher.cs',
  lineRange: [48, 319],
  summary: 'Implements IAlbumPrefetcher: debounce-batches album prefetch requests, resolves metadata via the metadata store, converts to partial AlbumDetailResult records, and publishes AlbumMetadataPrefetchedMessage.',
  tags: ['service', 'prefetch', 'album', 'metadata'],
  complexity: 'complex'
});
edges.push({ source: 'file:src/Wavee.UI.WinUI/Services/AlbumPrefetcher.cs', target: 'class:src/Wavee.UI.WinUI/Services/AlbumPrefetcher.cs:AlbumPrefetcher', type: 'contains', direction: 'forward', weight: 1.0 });
edges.push({ source: 'file:src/Wavee.UI.WinUI/Services/AlbumPrefetcher.cs', target: 'class:src/Wavee.UI.WinUI/Services/AlbumPrefetcher.cs:AlbumPrefetcher', type: 'exports', direction: 'forward', weight: 0.8 });

// 17. AppFeatureFlags.cs
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/Services/AppFeatureFlags.cs',
  type: 'file',
  name: 'AppFeatureFlags.cs',
  filePath: 'src/Wavee.UI.WinUI/Services/AppFeatureFlags.cs',
  summary: 'Reads app-wide feature flags from environment variables; currently exposes LocalFilesEnabled parsed from WAVEE_LOCAL_FILES.',
  tags: ['service', 'feature-flags', 'configuration'],
  complexity: 'simple'
});

// 18. AppInitializationService.cs
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/Services/AppInitializationService.cs',
  type: 'file',
  name: 'AppInitializationService.cs',
  filePath: 'src/Wavee.UI.WinUI/Services/AppInitializationService.cs',
  summary: 'Orchestrates app startup: restores the auth session, optionally loads demo playback, and shows a welcome notification on first run.',
  tags: ['service', 'initialization', 'startup'],
  complexity: 'moderate'
});
nodes.push({
  id: 'class:src/Wavee.UI.WinUI/Services/AppInitializationService.cs:AppInitializationService',
  type: 'class',
  name: 'AppInitializationService',
  filePath: 'src/Wavee.UI.WinUI/Services/AppInitializationService.cs',
  lineRange: [15, 80],
  summary: 'Coordinates post-launch initialisation: calls TryRestoreSessionAsync, triggers demo playback if configured, and shows a welcome notification.',
  tags: ['service', 'initialization', 'startup'],
  complexity: 'moderate'
});
edges.push({ source: 'file:src/Wavee.UI.WinUI/Services/AppInitializationService.cs', target: 'class:src/Wavee.UI.WinUI/Services/AppInitializationService.cs:AppInitializationService', type: 'contains', direction: 'forward', weight: 1.0 });
edges.push({ source: 'file:src/Wavee.UI.WinUI/Services/AppInitializationService.cs', target: 'class:src/Wavee.UI.WinUI/Services/AppInitializationService.cs:AppInitializationService', type: 'exports', direction: 'forward', weight: 0.8 });

// 19. AppLocalizationService.cs
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/Services/AppLocalizationService.cs',
  type: 'file',
  name: 'AppLocalizationService.cs',
  filePath: 'src/Wavee.UI.WinUI/Services/AppLocalizationService.cs',
  summary: 'Localization abstraction: IAppLocalizationService interface, a thin AppLocalizationService adapter, and AppLocalization static class for loading strings from WinRT ResourceLoader with language-override support.',
  tags: ['service', 'localization', 'i18n'],
  complexity: 'moderate'
});

// 20. ArtistBioSummarizer.cs
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/Services/ArtistBioSummarizer.cs',
  type: 'file',
  name: 'ArtistBioSummarizer.cs',
  filePath: 'src/Wavee.UI.WinUI/Services/ArtistBioSummarizer.cs',
  summary: 'On-device AI service that summarizes artist biographies using Phi Silica via PhiSilicaStructuredTextPipeline, with per-artist request deduplication and caching.',
  tags: ['service', 'ai', 'artist-bio', 'phi-silica'],
  complexity: 'complex'
});
nodes.push({
  id: 'class:src/Wavee.UI.WinUI/Services/ArtistBioSummarizer.cs:ArtistBioSummarizer',
  type: 'class',
  name: 'ArtistBioSummarizer',
  filePath: 'src/Wavee.UI.WinUI/Services/ArtistBioSummarizer.cs',
  lineRange: [28, 242],
  summary: 'Deduplicates concurrent bio-summarization requests per artist URI, ensures the Phi Silica language model is ready, generates structured prompts, and strips markdown artefacts from the output.',
  tags: ['service', 'ai', 'artist-bio'],
  complexity: 'complex'
});
edges.push({ source: 'file:src/Wavee.UI.WinUI/Services/ArtistBioSummarizer.cs', target: 'class:src/Wavee.UI.WinUI/Services/ArtistBioSummarizer.cs:ArtistBioSummarizer', type: 'contains', direction: 'forward', weight: 1.0 });
edges.push({ source: 'file:src/Wavee.UI.WinUI/Services/ArtistBioSummarizer.cs', target: 'class:src/Wavee.UI.WinUI/Services/ArtistBioSummarizer.cs:ArtistBioSummarizer', type: 'exports', direction: 'forward', weight: 0.8 });

// 21. BrowseResponseMapper.cs
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/Services/BrowseResponseMapper.cs',
  type: 'file',
  name: 'BrowseResponseMapper.cs',
  filePath: 'src/Wavee.UI.WinUI/Services/BrowseResponseMapper.cs',
  summary: 'Maps SpClient browse API responses to UI view-model records (HomeSection shelves, BrowseAllGroup categories, HomeSectionItem cards) for the Browse page.',
  tags: ['service', 'mapper', 'browse', 'data-model'],
  complexity: 'complex'
});
nodes.push({
  id: 'class:src/Wavee.UI.WinUI/Services/BrowseResponseMapper.cs:BrowseResponseMapper',
  type: 'class',
  name: 'BrowseResponseMapper',
  filePath: 'src/Wavee.UI.WinUI/Services/BrowseResponseMapper.cs',
  lineRange: [38, 230],
  summary: 'Static mapper converting Spotify browse API section entries into typed HomeSectionItem and BrowseAllGroup records for playlist, album, podcast, and category-subpage entry types.',
  tags: ['service', 'mapper', 'browse'],
  complexity: 'complex'
});
edges.push({ source: 'file:src/Wavee.UI.WinUI/Services/BrowseResponseMapper.cs', target: 'class:src/Wavee.UI.WinUI/Services/BrowseResponseMapper.cs:BrowseResponseMapper', type: 'contains', direction: 'forward', weight: 1.0 });
edges.push({ source: 'file:src/Wavee.UI.WinUI/Services/BrowseResponseMapper.cs', target: 'class:src/Wavee.UI.WinUI/Services/BrowseResponseMapper.cs:BrowseResponseMapper', type: 'exports', direction: 'forward', weight: 0.8 });

// 22. CachedImage.cs
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/Services/CachedImage.cs',
  type: 'file',
  name: 'CachedImage.cs',
  filePath: 'src/Wavee.UI.WinUI/Services/CachedImage.cs',
  summary: 'Wraps a LoadedImageSurface with its source URL, decode pixel size, load/error state, and a multi-subscriber load-completed event, acting as the cache entry for the image cache.',
  tags: ['service', 'image-cache', 'utility'],
  complexity: 'moderate'
});
nodes.push({
  id: 'class:src/Wavee.UI.WinUI/Services/CachedImage.cs:CachedImage',
  type: 'class',
  name: 'CachedImage',
  filePath: 'src/Wavee.UI.WinUI/Services/CachedImage.cs',
  lineRange: [20, 127],
  summary: 'Image cache entry holding a LoadedImageSurface alongside load metadata; propagates load-completed events to late subscribers and disposes the surface on cleanup.',
  tags: ['service', 'image-cache'],
  complexity: 'moderate'
});
edges.push({ source: 'file:src/Wavee.UI.WinUI/Services/CachedImage.cs', target: 'class:src/Wavee.UI.WinUI/Services/CachedImage.cs:CachedImage', type: 'contains', direction: 'forward', weight: 1.0 });
edges.push({ source: 'file:src/Wavee.UI.WinUI/Services/CachedImage.cs', target: 'class:src/Wavee.UI.WinUI/Services/CachedImage.cs:CachedImage', type: 'exports', direction: 'forward', weight: 0.8 });

// 23. DataServiceConfiguration.cs
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/Services/Data/DataServiceConfiguration.cs',
  type: 'file',
  name: 'DataServiceConfiguration.cs',
  filePath: 'src/Wavee.UI.WinUI/Services/Data/DataServiceConfiguration.cs',
  summary: 'Configuration object for data services that controls whether the app runs in demo mode, with a change notification event.',
  tags: ['configuration', 'service', 'demo-mode'],
  complexity: 'simple'
});

// 24. MockLibraryDataService.cs
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/Services/Data/MockLibraryDataService.cs',
  type: 'file',
  name: 'MockLibraryDataService.cs',
  filePath: 'src/Wavee.UI.WinUI/Services/Data/MockLibraryDataService.cs',
  summary: 'In-memory mock implementation of the library data service that generates realistic fake data (albums, artists, playlists, liked songs, podcasts, episodes) for demo and design-time use.',
  tags: ['service', 'mock', 'test', 'demo-mode'],
  complexity: 'complex'
});
nodes.push({
  id: 'class:src/Wavee.UI.WinUI/Services/Data/MockLibraryDataService.cs:MockLibraryDataService',
  type: 'class',
  name: 'MockLibraryDataService',
  filePath: 'src/Wavee.UI.WinUI/Services/Data/MockLibraryDataService.cs',
  lineRange: [15, 800],
  summary: 'Implements ILibraryDataService with fully generated mock data: pre-fills playlists, albums, artists, liked songs, podcast episodes, and playlist tracks at construction time.',
  tags: ['service', 'mock', 'demo-mode'],
  complexity: 'complex'
});
edges.push({ source: 'file:src/Wavee.UI.WinUI/Services/Data/MockLibraryDataService.cs', target: 'class:src/Wavee.UI.WinUI/Services/Data/MockLibraryDataService.cs:MockLibraryDataService', type: 'contains', direction: 'forward', weight: 1.0 });
edges.push({ source: 'file:src/Wavee.UI.WinUI/Services/Data/MockLibraryDataService.cs', target: 'class:src/Wavee.UI.WinUI/Services/Data/MockLibraryDataService.cs:MockLibraryDataService', type: 'exports', direction: 'forward', weight: 0.8 });

// 25. DispatcherQueueUiDispatcher.cs
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/Services/DispatcherQueueUiDispatcher.cs',
  type: 'file',
  name: 'DispatcherQueueUiDispatcher.cs',
  filePath: 'src/Wavee.UI.WinUI/Services/DispatcherQueueUiDispatcher.cs',
  summary: 'Adapter that wraps WinUI DispatcherQueue to implement the framework-neutral IUiDispatcher interface used by Wavee.UI services.',
  tags: ['service', 'adapter', 'threading', 'dispatcher'],
  complexity: 'simple'
});

const output = { nodes, edges };
console.log('nodeCount:', nodes.length);
console.log('edgeCount:', edges.length);

fs.writeFileSync(
  'C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-43.json',
  JSON.stringify(output, null, 2)
);
console.log('Written batch-43.json');
