const fs = require('fs');

const nodes = [];
const edges = [];

// ---- FILE NODES ----
nodes.push({id:'file:src/Wavee.UI.WinUI/Services/SpotifyMetadataLanguageSettings.cs',type:'file',name:'SpotifyMetadataLanguageSettings.cs',filePath:'src/Wavee.UI.WinUI/Services/SpotifyMetadataLanguageSettings.cs',summary:'Normalizes and resolves Spotify metadata locale/language settings, mapping app locale codes to Spotify-compatible language strings.',tags:['service','utility','configuration'],complexity:'simple'});
nodes.push({id:'file:src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayer.cs',type:'file',name:'SpotifyWebEmePlayer.cs',filePath:'src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayer.cs',summary:'Hosts a WebView2-based EME (Encrypted Media Extensions) player for Spotify video content, managing lifecycle, license requests, and playback state via web messages.',tags:['service','component','event-handler'],complexity:'complex'});
nodes.push({id:'file:src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayerDocumentRenderer.cs',type:'file',name:'SpotifyWebEmePlayerDocumentRenderer.cs',filePath:'src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayerDocumentRenderer.cs',summary:'Loads and renders the WebEmePlayer HTML template, substituting playback config, start position, and autoplay placeholders before injection into WebView2.',tags:['service','utility','component'],complexity:'simple'});
nodes.push({id:'file:src/Wavee.UI.WinUI/Services/SpotifyVideo/WebEmePlayer.html',type:'file',name:'WebEmePlayer.html',filePath:'src/Wavee.UI.WinUI/Services/SpotifyVideo/WebEmePlayer.html',summary:'Self-contained HTML/JavaScript page implementing Widevine/EME video playback in WebView2, handling DRM license requests, adaptive streaming, and bidirectional messaging with the host app.',tags:['component','markup'],complexity:'complex'});
nodes.push({id:'file:src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs',type:'file',name:'SpotifyVideoProvider.cs',filePath:'src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs',summary:'Central video playback provider orchestrating both native MediaPlayer (PlayReady) and WebView2 EME paths for Spotify video, managing quality selection, state transitions, adaptive streaming, and PlayReady protection initialization.',tags:['service','component','event-handler'],complexity:'complex'});
nodes.push({id:'file:src/Wavee.UI.WinUI/Services/ThemeColorService.cs',type:'file',name:'ThemeColorService.cs',filePath:'src/Wavee.UI.WinUI/Services/ThemeColorService.cs',summary:'Resolves and caches WinUI theme brush resources (text, accent, card, stroke) from the XAML resource dictionary, refreshing on theme changes.',tags:['service','utility'],complexity:'moderate'});
nodes.push({id:'file:src/Wavee.UI.WinUI/Services/ThemeService.cs',type:'file',name:'ThemeService.cs',filePath:'src/Wavee.UI.WinUI/Services/ThemeService.cs',summary:'Manages the application light/dark theme, persisting selection to app settings and applying it to the root XAML element.',tags:['service','configuration'],complexity:'simple'});
nodes.push({id:'file:src/Wavee.UI.WinUI/Services/TrackMetadataEnricher.cs',type:'file',name:'TrackMetadataEnricher.cs',filePath:'src/Wavee.UI.WinUI/Services/TrackMetadataEnricher.cs',summary:'Enriches track/episode queue items with artwork, artist top-tracks, and extended metadata by dispatching Spotify API calls and broadcasting results via MVVM messenger.',tags:['service','event-handler'],complexity:'complex'});
nodes.push({id:'file:src/Wavee.UI.WinUI/Services/UiHealthMonitor.cs',type:'file',name:'UiHealthMonitor.cs',filePath:'src/Wavee.UI.WinUI/Services/UiHealthMonitor.cs',summary:'Monitors UI thread health by sampling frame durations and GC metrics, detecting stalls and generating diagnostic reports for the debug overlay.',tags:['service','utility'],complexity:'complex'});
nodes.push({id:'file:src/Wavee.UI.WinUI/Services/UiOperationProfiler.cs',type:'file',name:'UiOperationProfiler.cs',filePath:'src/Wavee.UI.WinUI/Services/UiOperationProfiler.cs',summary:'Profiles named UI operations by recording durations and GC statistics, maintaining a top-slowest heap and generating aggregate performance reports.',tags:['service','utility'],complexity:'complex'});
nodes.push({id:'file:src/Wavee.UI.WinUI/Services/UpdateService.cs',type:'file',name:'UpdateService.cs',filePath:'src/Wavee.UI.WinUI/Services/UpdateService.cs',summary:'Checks GitHub Releases for new versions of WaveeMusic, detects distribution channel (Store vs sideload), and exposes update status properties for the Settings UI.',tags:['service'],complexity:'moderate'});
nodes.push({id:'file:src/Wavee.UI.WinUI/Services/UserProfileResolver.cs',type:'file',name:'UserProfileResolver.cs',filePath:'src/Wavee.UI.WinUI/Services/UserProfileResolver.cs',summary:'Resolves Spotify user URIs/IDs to display names and profile data via the extended metadata API, with in-memory caching and inflight deduplication.',tags:['service','utility'],complexity:'moderate'});
nodes.push({id:'file:src/Wavee.UI.WinUI/Services/UserScopeGuard.cs',type:'file',name:'UserScopeGuard.cs',filePath:'src/Wavee.UI.WinUI/Services/UserScopeGuard.cs',summary:'Guards per-user data isolation on login, clearing playlists, liked tracks, and profile caches when the authenticated Spotify user ID changes.',tags:['service'],complexity:'moderate'});
nodes.push({id:'file:src/Wavee.UI.WinUI/Services/VideoAutoNavigationSuppressor.cs',type:'file',name:'VideoAutoNavigationSuppressor.cs',filePath:'src/Wavee.UI.WinUI/Services/VideoAutoNavigationSuppressor.cs',summary:'Static gate that suppresses the next automatic local-video navigation event within a short expiry window, preventing duplicate navigation from playback resume.',tags:['service','utility'],complexity:'simple'});
nodes.push({id:'file:src/Wavee.UI.WinUI/Services/WindowsVideoThumbnailExtractor.cs',type:'file',name:'WindowsVideoThumbnailExtractor.cs',filePath:'src/Wavee.UI.WinUI/Services/WindowsVideoThumbnailExtractor.cs',summary:'Extracts thumbnail images from local video files using the Windows StorageFile thumbnail API, returning a BitmapImage at a configurable size.',tags:['service','utility'],complexity:'simple'});
nodes.push({id:'file:src/Wavee.UI.WinUI/Shaders/MeshGradientShader.cs',type:'file',name:'MeshGradientShader.cs',filePath:'src/Wavee.UI.WinUI/Shaders/MeshGradientShader.cs',summary:'ComputeSharp D2D1 pixel shader producing an animated mesh gradient from primary and accent colors for use in hero backgrounds.',tags:['component','utility'],complexity:'simple',languageNotes:'Uses ComputeSharp ID2D1PixelShader with HLSL-style intrinsics compiled by ComputeSharp at build time.'});
nodes.push({id:'file:src/Wavee.UI.WinUI/Strings/en-US/Resources.resw',type:'file',name:'Resources.resw (en-US)',filePath:'src/Wavee.UI.WinUI/Strings/en-US/Resources.resw',summary:'English (US) string resource table for the WaveeMusic WinUI app, containing all localizable UI strings.',tags:['configuration','data-model'],complexity:'complex'});
nodes.push({id:'file:src/Wavee.UI.WinUI/Strings/ko-KR/Resources.resw',type:'file',name:'Resources.resw (ko-KR)',filePath:'src/Wavee.UI.WinUI/Strings/ko-KR/Resources.resw',summary:'Korean (KR) string resource table for the WaveeMusic WinUI app, providing Korean localization of all UI strings.',tags:['configuration','data-model'],complexity:'complex'});
nodes.push({id:'file:src/Wavee.UI.WinUI/Styles/ButtonStyles.xaml',type:'file',name:'ButtonStyles.xaml',filePath:'src/Wavee.UI.WinUI/Styles/ButtonStyles.xaml',summary:'XAML resource dictionary defining custom button styles for the WinUI app, including playback, icon, and control button variants.',tags:['component','markup'],complexity:'complex'});
nodes.push({id:'file:src/Wavee.UI.WinUI/Styles/CardStyles.xaml',type:'file',name:'CardStyles.xaml',filePath:'src/Wavee.UI.WinUI/Styles/CardStyles.xaml',summary:'XAML resource dictionary defining card container styles used by ContentCard and related shelf components.',tags:['component','markup'],complexity:'simple'});
nodes.push({id:'file:src/Wavee.UI.WinUI/Styles/FluentGlyphs.cs',type:'file',name:'FluentGlyphs.cs',filePath:'src/Wavee.UI.WinUI/Styles/FluentGlyphs.cs',summary:'Single source of truth for Fluent UI icon glyph codepoints used throughout the app, plus a helper to resolve social platform icons from URL/name.',tags:['utility','type-definition'],complexity:'moderate'});
nodes.push({id:'file:src/Wavee.UI.WinUI/Styles/FontResources.xaml',type:'file',name:'FontResources.xaml',filePath:'src/Wavee.UI.WinUI/Styles/FontResources.xaml',summary:'Minimal XAML resource dictionary declaring font family resources referenced by other style dictionaries.',tags:['configuration','markup'],complexity:'simple'});
nodes.push({id:'file:src/Wavee.UI.WinUI/Styles/ListViewStyles.xaml',type:'file',name:'ListViewStyles.xaml',filePath:'src/Wavee.UI.WinUI/Styles/ListViewStyles.xaml',summary:'XAML resource dictionary with custom ListView and ListViewItem styles for track lists and library views.',tags:['component','markup'],complexity:'simple'});
nodes.push({id:'file:src/Wavee.UI.WinUI/Styles/PlayerBarStyles.xaml',type:'file',name:'PlayerBarStyles.xaml',filePath:'src/Wavee.UI.WinUI/Styles/PlayerBarStyles.xaml',summary:'XAML resource dictionary defining styles for the bottom player bar controls including transport buttons and progress slider.',tags:['component','markup'],complexity:'moderate'});
nodes.push({id:'file:src/Wavee.UI.WinUI/Styles/TabBarStyles.xaml',type:'file',name:'TabBarStyles.xaml',filePath:'src/Wavee.UI.WinUI/Styles/TabBarStyles.xaml',summary:'XAML resource dictionary with comprehensive tab bar and navigation item styles including Fluent animations and selection indicators.',tags:['component','markup'],complexity:'complex'});

// ---- CLASS NODES ----
nodes.push({id:'class:src/Wavee.UI.WinUI/Services/SpotifyMetadataLanguageSettings.cs:SpotifyMetadataLanguageSettings',type:'class',name:'SpotifyMetadataLanguageSettings',filePath:'src/Wavee.UI.WinUI/Services/SpotifyMetadataLanguageSettings.cs',lineRange:[6,69],summary:'Static class normalizing Spotify metadata language settings and resolving effective locale codes for API requests.',tags:['utility','service'],complexity:'simple'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/SpotifyMetadataLanguageSettings.cs',target:'class:src/Wavee.UI.WinUI/Services/SpotifyMetadataLanguageSettings.cs:SpotifyMetadataLanguageSettings',type:'contains',direction:'forward',weight:1.0});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/SpotifyMetadataLanguageSettings.cs',target:'class:src/Wavee.UI.WinUI/Services/SpotifyMetadataLanguageSettings.cs:SpotifyMetadataLanguageSettings',type:'exports',direction:'forward',weight:0.8});

nodes.push({id:'class:src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayer.cs:SpotifyWebEmePlayer',type:'class',name:'SpotifyWebEmePlayer',filePath:'src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayer.cs',lineRange:[18,444],summary:'WebView2-hosted EME video player managing async startup, DRM license acquisition, playback control commands, and surface/state event forwarding to the host provider.',tags:['service','component','event-handler'],complexity:'complex'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayer.cs',target:'class:src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayer.cs:SpotifyWebEmePlayer',type:'contains',direction:'forward',weight:1.0});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayer.cs',target:'class:src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayer.cs:SpotifyWebEmePlayer',type:'exports',direction:'forward',weight:0.8});

nodes.push({id:'class:src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayerDocumentRenderer.cs:SpotifyWebEmePlayerDocumentRenderer',type:'class',name:'SpotifyWebEmePlayerDocumentRenderer',filePath:'src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayerDocumentRenderer.cs',lineRange:[11,59],summary:'Lazily loads the WebEmePlayer HTML template from disk and renders it with video config and playback parameters substituted.',tags:['service','utility'],complexity:'simple'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayerDocumentRenderer.cs',target:'class:src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayerDocumentRenderer.cs:SpotifyWebEmePlayerDocumentRenderer',type:'contains',direction:'forward',weight:1.0});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayerDocumentRenderer.cs',target:'class:src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayerDocumentRenderer.cs:SpotifyWebEmePlayerDocumentRenderer',type:'exports',direction:'forward',weight:0.8});

nodes.push({id:'class:src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs:SpotifyVideoProvider',type:'class',name:'SpotifyVideoProvider',filePath:'src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs',lineRange:[38,1681],summary:'Top-level video playback provider supporting both PlayReady/native MediaPlayer and WebView2/EME paths, orchestrating DRM init-segment protection fetching, quality selection, state publishing, and error recovery.',tags:['service','component','event-handler'],complexity:'complex'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs',target:'class:src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs:SpotifyVideoProvider',type:'contains',direction:'forward',weight:1.0});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs',target:'class:src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs:SpotifyVideoProvider',type:'exports',direction:'forward',weight:0.8});

nodes.push({id:'class:src/Wavee.UI.WinUI/Services/ThemeColorService.cs:ThemeColorService',type:'class',name:'ThemeColorService',filePath:'src/Wavee.UI.WinUI/Services/ThemeColorService.cs',lineRange:[15,162],summary:'Singleton service exposing resolved SolidColorBrush properties for themed colors, rebuilding from XAML ResourceDictionary on theme change.',tags:['service','utility'],complexity:'moderate'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/ThemeColorService.cs',target:'class:src/Wavee.UI.WinUI/Services/ThemeColorService.cs:ThemeColorService',type:'contains',direction:'forward',weight:1.0});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/ThemeColorService.cs',target:'class:src/Wavee.UI.WinUI/Services/ThemeColorService.cs:ThemeColorService',type:'exports',direction:'forward',weight:0.8});

nodes.push({id:'class:src/Wavee.UI.WinUI/Services/ThemeService.cs:ThemeService',type:'class',name:'ThemeService',filePath:'src/Wavee.UI.WinUI/Services/ThemeService.cs',lineRange:[9,48],summary:'Applies and persists the WinUI app theme (Light/Dark/Default) to the root FrameworkElement.',tags:['service'],complexity:'simple'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/ThemeService.cs',target:'class:src/Wavee.UI.WinUI/Services/ThemeService.cs:ThemeService',type:'contains',direction:'forward',weight:1.0});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/ThemeService.cs',target:'class:src/Wavee.UI.WinUI/Services/ThemeService.cs:ThemeService',type:'exports',direction:'forward',weight:0.8});

nodes.push({id:'class:src/Wavee.UI.WinUI/Services/TrackMetadataEnricher.cs:TrackMetadataEnricher',type:'class',name:'TrackMetadataEnricher',filePath:'src/Wavee.UI.WinUI/Services/TrackMetadataEnricher.cs',lineRange:[25,650],summary:'Background service receiving MVVM messenger messages to enrich queue tracks and episodes with images, artist extended top-tracks, and episode metadata via batched Spotify API calls.',tags:['service','event-handler'],complexity:'complex'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/TrackMetadataEnricher.cs',target:'class:src/Wavee.UI.WinUI/Services/TrackMetadataEnricher.cs:TrackMetadataEnricher',type:'contains',direction:'forward',weight:1.0});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/TrackMetadataEnricher.cs',target:'class:src/Wavee.UI.WinUI/Services/TrackMetadataEnricher.cs:TrackMetadataEnricher',type:'exports',direction:'forward',weight:0.8});

nodes.push({id:'class:src/Wavee.UI.WinUI/Services/UiHealthMonitor.cs:UiHealthMonitor',type:'class',name:'UiHealthMonitor',filePath:'src/Wavee.UI.WinUI/Services/UiHealthMonitor.cs',lineRange:[16,372],summary:'Sampling-based UI health monitor tracking frame durations, GC pressure, and stall counts via CompositionTarget.Rendering and a DispatcherTimer.',tags:['service','utility'],complexity:'complex'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/UiHealthMonitor.cs',target:'class:src/Wavee.UI.WinUI/Services/UiHealthMonitor.cs:UiHealthMonitor',type:'contains',direction:'forward',weight:1.0});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/UiHealthMonitor.cs',target:'class:src/Wavee.UI.WinUI/Services/UiHealthMonitor.cs:UiHealthMonitor',type:'exports',direction:'forward',weight:0.8});

nodes.push({id:'class:src/Wavee.UI.WinUI/Services/UiOperationProfiler.cs:UiOperationProfiler',type:'class',name:'UiOperationProfiler',filePath:'src/Wavee.UI.WinUI/Services/UiOperationProfiler.cs',lineRange:[14,288],summary:'Singleton operation profiler recording named UI operation durations into per-name stats and a top-slowest heap for the performance debug overlay.',tags:['service','utility','singleton'],complexity:'complex'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/UiOperationProfiler.cs',target:'class:src/Wavee.UI.WinUI/Services/UiOperationProfiler.cs:UiOperationProfiler',type:'contains',direction:'forward',weight:1.0});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/UiOperationProfiler.cs',target:'class:src/Wavee.UI.WinUI/Services/UiOperationProfiler.cs:UiOperationProfiler',type:'exports',direction:'forward',weight:0.8});

nodes.push({id:'class:src/Wavee.UI.WinUI/Services/UpdateService.cs:UpdateService',type:'class',name:'UpdateService',filePath:'src/Wavee.UI.WinUI/Services/UpdateService.cs',lineRange:[18,235],summary:'Polls GitHub Releases API to detect app updates, comparing semantic versions and exposing changelog, release URL, and update-available flag.',tags:['service'],complexity:'moderate'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/UpdateService.cs',target:'class:src/Wavee.UI.WinUI/Services/UpdateService.cs:UpdateService',type:'contains',direction:'forward',weight:1.0});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/UpdateService.cs',target:'class:src/Wavee.UI.WinUI/Services/UpdateService.cs:UpdateService',type:'exports',direction:'forward',weight:0.8});

nodes.push({id:'class:src/Wavee.UI.WinUI/Services/UserProfileResolver.cs:IUserProfileResolver',type:'class',name:'IUserProfileResolver',filePath:'src/Wavee.UI.WinUI/Services/UserProfileResolver.cs',lineRange:[26,41],summary:'Interface contract for resolving Spotify user URIs to display names and profile data.',tags:['type-definition','service'],complexity:'simple'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/UserProfileResolver.cs',target:'class:src/Wavee.UI.WinUI/Services/UserProfileResolver.cs:IUserProfileResolver',type:'contains',direction:'forward',weight:1.0});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/UserProfileResolver.cs',target:'class:src/Wavee.UI.WinUI/Services/UserProfileResolver.cs:IUserProfileResolver',type:'exports',direction:'forward',weight:0.8});

nodes.push({id:'class:src/Wavee.UI.WinUI/Services/UserProfileResolver.cs:UserProfileResolver',type:'class',name:'UserProfileResolver',filePath:'src/Wavee.UI.WinUI/Services/UserProfileResolver.cs',lineRange:[49,166],summary:'Implements IUserProfileResolver with in-memory cache and inflight deduplication for Spotify user metadata API calls.',tags:['service'],complexity:'moderate'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/UserProfileResolver.cs',target:'class:src/Wavee.UI.WinUI/Services/UserProfileResolver.cs:UserProfileResolver',type:'contains',direction:'forward',weight:1.0});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/UserProfileResolver.cs',target:'class:src/Wavee.UI.WinUI/Services/UserProfileResolver.cs:UserProfileResolver',type:'exports',direction:'forward',weight:0.8});
edges.push({source:'class:src/Wavee.UI.WinUI/Services/UserProfileResolver.cs:UserProfileResolver',target:'class:src/Wavee.UI.WinUI/Services/UserProfileResolver.cs:IUserProfileResolver',type:'implements',direction:'forward',weight:0.9});

nodes.push({id:'class:src/Wavee.UI.WinUI/Services/UserScopeGuard.cs:UserScopeGuard',type:'class',name:'UserScopeGuard',filePath:'src/Wavee.UI.WinUI/Services/UserScopeGuard.cs',lineRange:[12,111],summary:'Ensures user-scoped data isolation by clearing and re-initializing caches when the logged-in Spotify user changes.',tags:['service'],complexity:'moderate'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/UserScopeGuard.cs',target:'class:src/Wavee.UI.WinUI/Services/UserScopeGuard.cs:UserScopeGuard',type:'contains',direction:'forward',weight:1.0});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/UserScopeGuard.cs',target:'class:src/Wavee.UI.WinUI/Services/UserScopeGuard.cs:UserScopeGuard',type:'exports',direction:'forward',weight:0.8});

nodes.push({id:'class:src/Wavee.UI.WinUI/Services/VideoAutoNavigationSuppressor.cs:VideoAutoNavigationSuppressor',type:'class',name:'VideoAutoNavigationSuppressor',filePath:'src/Wavee.UI.WinUI/Services/VideoAutoNavigationSuppressor.cs',lineRange:[5,56],summary:'Static suppressor that prevents duplicate automatic video navigation by consuming a timed suppression token keyed to a track URI.',tags:['service','utility'],complexity:'simple'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/VideoAutoNavigationSuppressor.cs',target:'class:src/Wavee.UI.WinUI/Services/VideoAutoNavigationSuppressor.cs:VideoAutoNavigationSuppressor',type:'contains',direction:'forward',weight:1.0});

nodes.push({id:'class:src/Wavee.UI.WinUI/Services/WindowsVideoThumbnailExtractor.cs:WindowsVideoThumbnailExtractor',type:'class',name:'WindowsVideoThumbnailExtractor',filePath:'src/Wavee.UI.WinUI/Services/WindowsVideoThumbnailExtractor.cs',lineRange:[24,83],summary:'Extracts BitmapImage thumbnails from local video files via Windows StorageFile/StorageItemThumbnail APIs.',tags:['service','utility'],complexity:'simple'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/WindowsVideoThumbnailExtractor.cs',target:'class:src/Wavee.UI.WinUI/Services/WindowsVideoThumbnailExtractor.cs:WindowsVideoThumbnailExtractor',type:'contains',direction:'forward',weight:1.0});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/WindowsVideoThumbnailExtractor.cs',target:'class:src/Wavee.UI.WinUI/Services/WindowsVideoThumbnailExtractor.cs:WindowsVideoThumbnailExtractor',type:'exports',direction:'forward',weight:0.8});

nodes.push({id:'class:src/Wavee.UI.WinUI/Styles/FluentGlyphs.cs:FluentGlyphs',type:'class',name:'FluentGlyphs',filePath:'src/Wavee.UI.WinUI/Styles/FluentGlyphs.cs',lineRange:[16,241],summary:'Static registry of Fluent icon PUA codepoints as named string constants, plus ResolveSocialIcon for mapping social platform URLs to their icon glyphs.',tags:['utility','type-definition'],complexity:'moderate'});
edges.push({source:'file:src/Wavee.UI.WinUI/Styles/FluentGlyphs.cs',target:'class:src/Wavee.UI.WinUI/Styles/FluentGlyphs.cs:FluentGlyphs',type:'contains',direction:'forward',weight:1.0});
edges.push({source:'file:src/Wavee.UI.WinUI/Styles/FluentGlyphs.cs',target:'class:src/Wavee.UI.WinUI/Styles/FluentGlyphs.cs:FluentGlyphs',type:'exports',direction:'forward',weight:0.8});

// ---- FUNCTION NODES ----
// SpotifyWebEmePlayer
nodes.push({id:'function:src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayer.cs:StartAsync',type:'function',name:'StartAsync',filePath:'src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayer.cs',lineRange:[63,120],summary:'Initializes the WebView2 control for EME playback and begins loading the player HTML document with the given config.',tags:['service','event-handler'],complexity:'complex'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayer.cs',target:'function:src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayer.cs:StartAsync',type:'contains',direction:'forward',weight:1.0});

nodes.push({id:'function:src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayer.cs:HandleLicenseRequestAsync',type:'function',name:'HandleLicenseRequestAsync',filePath:'src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayer.cs',lineRange:[294,328],summary:'Handles EME DRM license requests from the web player by forwarding challenge bytes to the Spotify license requester and returning the response.',tags:['service','event-handler'],complexity:'moderate'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayer.cs',target:'function:src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayer.cs:HandleLicenseRequestAsync',type:'contains',direction:'forward',weight:1.0});

nodes.push({id:'function:src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayer.cs:OnWebMessageReceived',type:'function',name:'OnWebMessageReceived',filePath:'src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayer.cs',lineRange:[205,267],summary:'Dispatches incoming web messages from the HTML player (state updates, license requests, errors, first-frame signals) to the appropriate handlers.',tags:['event-handler'],complexity:'complex'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayer.cs',target:'function:src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayer.cs:OnWebMessageReceived',type:'contains',direction:'forward',weight:1.0});

// SpotifyVideoProvider
nodes.push({id:'function:src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs:PlayAsync',type:'function',name:'PlayAsync',filePath:'src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs',lineRange:[136,272],summary:'Main playback entry point selecting between PlayReady native player and WebView2 EME path based on the video manifest content.',tags:['service','event-handler'],complexity:'complex'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs',target:'function:src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs:PlayAsync',type:'contains',direction:'forward',weight:1.0});

nodes.push({id:'function:src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs:StartWebEmePlaybackAsync',type:'function',name:'StartWebEmePlaybackAsync',filePath:'src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs',lineRange:[648,708],summary:'Starts or restarts the WebView2 EME player path with a given manifest, managing player creation, attachment, and initial seek.',tags:['service'],complexity:'complex'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs',target:'function:src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs:StartWebEmePlaybackAsync',type:'contains',direction:'forward',weight:1.0});

nodes.push({id:'function:src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs:FetchInitSegmentProtectionAsync',type:'function',name:'FetchInitSegmentProtectionAsync',filePath:'src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs',lineRange:[1206,1234],summary:'Fetches PlayReady init-segment protection data for all video profiles in the manifest, required for native MediaPlayer DRM setup.',tags:['service'],complexity:'moderate'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs',target:'function:src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs:FetchInitSegmentProtectionAsync',type:'contains',direction:'forward',weight:1.0});

nodes.push({id:'function:src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs:OnWebEmeStateChanged',type:'function',name:'OnWebEmeStateChanged',filePath:'src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs',lineRange:[814,863],summary:'Handles state change events from the EME web player and translates them to the unified video provider state observable.',tags:['event-handler'],complexity:'complex'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs',target:'function:src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs:OnWebEmeStateChanged',type:'contains',direction:'forward',weight:1.0});

nodes.push({id:'function:src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs:BuildProtectionManager',type:'function',name:'BuildProtectionManager',filePath:'src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs',lineRange:[1377,1415],summary:'Constructs a Windows MediaProtectionManager with PlayReady configuration from init-protection bytes for native adaptive streaming.',tags:['service'],complexity:'moderate'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs',target:'function:src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs:BuildProtectionManager',type:'contains',direction:'forward',weight:1.0});

nodes.push({id:'function:src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs:OnServiceRequested',type:'function',name:'OnServiceRequested',filePath:'src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs',lineRange:[1423,1490],summary:'Handles PlayReady MediaProtectionManager service requests by forwarding SOAP license challenges to the Spotify PlayReady license endpoint.',tags:['event-handler'],complexity:'complex'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs',target:'function:src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs:OnServiceRequested',type:'contains',direction:'forward',weight:1.0});

// TrackMetadataEnricher
nodes.push({id:'function:src/Wavee.UI.WinUI/Services/TrackMetadataEnricher.cs:EnrichTrackAsync',type:'function',name:'EnrichTrackAsync',filePath:'src/Wavee.UI.WinUI/Services/TrackMetadataEnricher.cs',lineRange:[91,152],summary:'Enriches a single track URI with extended artist top-tracks, images, and metadata, broadcasting results to queue consumers.',tags:['service'],complexity:'complex'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/TrackMetadataEnricher.cs',target:'function:src/Wavee.UI.WinUI/Services/TrackMetadataEnricher.cs:EnrichTrackAsync',type:'contains',direction:'forward',weight:1.0});

nodes.push({id:'function:src/Wavee.UI.WinUI/Services/TrackMetadataEnricher.cs:EnrichQueueTracksAsync',type:'function',name:'EnrichQueueTracksAsync',filePath:'src/Wavee.UI.WinUI/Services/TrackMetadataEnricher.cs',lineRange:[340,415],summary:'Batch-enriches a set of queue track URIs with images using SpClient batch calls, broadcasting per-track image results.',tags:['service'],complexity:'complex'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/TrackMetadataEnricher.cs',target:'function:src/Wavee.UI.WinUI/Services/TrackMetadataEnricher.cs:EnrichQueueTracksAsync',type:'contains',direction:'forward',weight:1.0});

nodes.push({id:'function:src/Wavee.UI.WinUI/Services/TrackMetadataEnricher.cs:LoadExtendedTopTracksCoreAsync',type:'function',name:'LoadExtendedTopTracksCoreAsync',filePath:'src/Wavee.UI.WinUI/Services/TrackMetadataEnricher.cs',lineRange:[206,290],summary:'Fetches the full extended top-tracks list for an artist via Pathfinder, iterating paginated results into a complete sorted list.',tags:['service'],complexity:'complex'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/TrackMetadataEnricher.cs',target:'function:src/Wavee.UI.WinUI/Services/TrackMetadataEnricher.cs:LoadExtendedTopTracksCoreAsync',type:'contains',direction:'forward',weight:1.0});

// UiHealthMonitor
nodes.push({id:'function:src/Wavee.UI.WinUI/Services/UiHealthMonitor.cs:OnTick',type:'function',name:'OnTick',filePath:'src/Wavee.UI.WinUI/Services/UiHealthMonitor.cs',lineRange:[125,217],summary:'Timer tick handler sampling GC generations, managed heap size, and frame durations, updating stall/critical counters and the rolling history ring buffer.',tags:['event-handler','utility'],complexity:'complex'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/UiHealthMonitor.cs',target:'function:src/Wavee.UI.WinUI/Services/UiHealthMonitor.cs:OnTick',type:'contains',direction:'forward',weight:1.0});

nodes.push({id:'function:src/Wavee.UI.WinUI/Services/UiHealthMonitor.cs:GenerateReport',type:'function',name:'GenerateReport',filePath:'src/Wavee.UI.WinUI/Services/UiHealthMonitor.cs',lineRange:[309,343],summary:'Generates a formatted diagnostic report string summarizing frame stats, GC activity, and stall counts for the debug overlay.',tags:['utility'],complexity:'moderate'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/UiHealthMonitor.cs',target:'function:src/Wavee.UI.WinUI/Services/UiHealthMonitor.cs:GenerateReport',type:'contains',direction:'forward',weight:1.0});

// UiOperationProfiler
nodes.push({id:'function:src/Wavee.UI.WinUI/Services/UiOperationProfiler.cs:AppendReport',type:'function',name:'AppendReport',filePath:'src/Wavee.UI.WinUI/Services/UiOperationProfiler.cs',lineRange:[105,156],summary:'Appends a formatted profiler report to a StringBuilder, listing per-operation stats and GC totals for the debug overlay.',tags:['utility'],complexity:'moderate'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/UiOperationProfiler.cs',target:'function:src/Wavee.UI.WinUI/Services/UiOperationProfiler.cs:AppendReport',type:'contains',direction:'forward',weight:1.0});

nodes.push({id:'function:src/Wavee.UI.WinUI/Services/UiOperationProfiler.cs:RecordOperation',type:'function',name:'RecordOperation',filePath:'src/Wavee.UI.WinUI/Services/UiOperationProfiler.cs',lineRange:[197,224],summary:'Records a completed operation duration into per-name running stats and conditionally logs if it exceeds the threshold.',tags:['utility'],complexity:'moderate'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/UiOperationProfiler.cs',target:'function:src/Wavee.UI.WinUI/Services/UiOperationProfiler.cs:RecordOperation',type:'contains',direction:'forward',weight:1.0});

// UpdateService
nodes.push({id:'function:src/Wavee.UI.WinUI/Services/UpdateService.cs:CheckForUpdateAsync',type:'function',name:'CheckForUpdateAsync',filePath:'src/Wavee.UI.WinUI/Services/UpdateService.cs',lineRange:[127,208],summary:'Downloads the latest GitHub release JSON, compares the version to the installed app version, and updates status/changelog properties.',tags:['service'],complexity:'complex'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/UpdateService.cs',target:'function:src/Wavee.UI.WinUI/Services/UpdateService.cs:CheckForUpdateAsync',type:'contains',direction:'forward',weight:1.0});

// UserProfileResolver
nodes.push({id:'function:src/Wavee.UI.WinUI/Services/UserProfileResolver.cs:ResolveAsync',type:'function',name:'ResolveAsync',filePath:'src/Wavee.UI.WinUI/Services/UserProfileResolver.cs',lineRange:[97,143],summary:'Resolves a normalized Spotify user URI to profile data, deduplicating inflight requests with a TaskCompletionSource dictionary.',tags:['service','utility'],complexity:'complex'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/UserProfileResolver.cs',target:'function:src/Wavee.UI.WinUI/Services/UserProfileResolver.cs:ResolveAsync',type:'contains',direction:'forward',weight:1.0});

// UserScopeGuard
nodes.push({id:'function:src/Wavee.UI.WinUI/Services/UserScopeGuard.cs:EnsureScopeAsync',type:'function',name:'EnsureScopeAsync',filePath:'src/Wavee.UI.WinUI/Services/UserScopeGuard.cs',lineRange:[35,74],summary:'Checks the on-disk user marker and clears all user-scoped caches/data if the current user ID differs from the stored one.',tags:['service'],complexity:'complex'});
edges.push({source:'file:src/Wavee.UI.WinUI/Services/UserScopeGuard.cs',target:'function:src/Wavee.UI.WinUI/Services/UserScopeGuard.cs:EnsureScopeAsync',type:'contains',direction:'forward',weight:1.0});

// FluentGlyphs
nodes.push({id:'function:src/Wavee.UI.WinUI/Styles/FluentGlyphs.cs:ResolveSocialIcon',type:'function',name:'ResolveSocialIcon',filePath:'src/Wavee.UI.WinUI/Styles/FluentGlyphs.cs',lineRange:[217,240],summary:'Maps a social platform URL or name to the corresponding Fluent icon glyph constant.',tags:['utility'],complexity:'simple'});
edges.push({source:'file:src/Wavee.UI.WinUI/Styles/FluentGlyphs.cs',target:'function:src/Wavee.UI.WinUI/Styles/FluentGlyphs.cs:ResolveSocialIcon',type:'contains',direction:'forward',weight:1.0});
edges.push({source:'file:src/Wavee.UI.WinUI/Styles/FluentGlyphs.cs',target:'function:src/Wavee.UI.WinUI/Styles/FluentGlyphs.cs:ResolveSocialIcon',type:'exports',direction:'forward',weight:0.8});

// No import edges: all batchImportData arrays are empty.

console.log('nodes:', nodes.length, 'edges:', edges.length);

// Split into 2 parts: sort files alphabetically, chunk into 2 groups
// Files in batch alphabetically:
const fileOrder = [
  'src/Wavee.UI.WinUI/Services/SpotifyMetadataLanguageSettings.cs',
  'src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayer.cs',
  'src/Wavee.UI.WinUI/Services/SpotifyVideo/SpotifyWebEmePlayerDocumentRenderer.cs',
  'src/Wavee.UI.WinUI/Services/SpotifyVideo/WebEmePlayer.html',
  'src/Wavee.UI.WinUI/Services/SpotifyVideoProvider.cs',
  'src/Wavee.UI.WinUI/Services/ThemeColorService.cs',
  'src/Wavee.UI.WinUI/Services/ThemeService.cs',
  'src/Wavee.UI.WinUI/Services/TrackMetadataEnricher.cs',
  'src/Wavee.UI.WinUI/Services/UiHealthMonitor.cs',
  'src/Wavee.UI.WinUI/Services/UiOperationProfiler.cs',
  'src/Wavee.UI.WinUI/Services/UpdateService.cs',
  'src/Wavee.UI.WinUI/Services/UserProfileResolver.cs',
  'src/Wavee.UI.WinUI/Services/UserScopeGuard.cs',
  'src/Wavee.UI.WinUI/Services/VideoAutoNavigationSuppressor.cs',
  'src/Wavee.UI.WinUI/Services/WindowsVideoThumbnailExtractor.cs',
  'src/Wavee.UI.WinUI/Shaders/MeshGradientShader.cs',
  'src/Wavee.UI.WinUI/Strings/en-US/Resources.resw',
  'src/Wavee.UI.WinUI/Strings/ko-KR/Resources.resw',
  'src/Wavee.UI.WinUI/Styles/ButtonStyles.xaml',
  'src/Wavee.UI.WinUI/Styles/CardStyles.xaml',
  'src/Wavee.UI.WinUI/Styles/FluentGlyphs.cs',
  'src/Wavee.UI.WinUI/Styles/FontResources.xaml',
  'src/Wavee.UI.WinUI/Styles/ListViewStyles.xaml',
  'src/Wavee.UI.WinUI/Styles/PlayerBarStyles.xaml',
  'src/Wavee.UI.WinUI/Styles/TabBarStyles.xaml'
];

// 2 parts: first 13 files in part 1, last 12 in part 2
const part1Files = new Set(fileOrder.slice(0, 13));
const part2Files = new Set(fileOrder.slice(13));

function getNodeFile(n) {
  if (n.filePath) return n.filePath;
  // for sub-file nodes derive from id
  const id = n.id;
  const prefix = id.split(':')[0];
  const rest = id.slice(prefix.length + 1);
  const pathEnd = rest.lastIndexOf(':');
  return pathEnd >= 0 ? rest.slice(0, pathEnd) : rest;
}

const nodes1 = nodes.filter(n => part1Files.has(getNodeFile(n)));
const nodes2 = nodes.filter(n => part2Files.has(getNodeFile(n)));

const nodeIds1 = new Set(nodes1.map(n => n.id));
const nodeIds2 = new Set(nodes2.map(n => n.id));

const edges1 = edges.filter(e => nodeIds1.has(e.source));
const edges2 = edges.filter(e => nodeIds2.has(e.source));

console.log('Part 1: nodes=' + nodes1.length + ' edges=' + edges1.length);
console.log('Part 2: nodes=' + nodes2.length + ' edges=' + edges2.length);

fs.writeFileSync(
  'C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-47-part-1.json',
  JSON.stringify({nodes: nodes1, edges: edges1}, null, 2),
  {encoding: 'utf8'}
);
fs.writeFileSync(
  'C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-47-part-2.json',
  JSON.stringify({nodes: nodes2, edges: edges2}, null, 2),
  {encoding: 'utf8'}
);
console.log('Written batch-47-part-1.json and batch-47-part-2.json');

// Remove the single-file output if present
try { fs.unlinkSync('C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-47.json'); } catch(e) {}
console.log('Removed batch-47.json');
