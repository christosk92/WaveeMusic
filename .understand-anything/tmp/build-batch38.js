const fs = require('fs');
const data = JSON.parse(fs.readFileSync('C:/WAVEE/WaveeMusic/.understand-anything/tmp/ua-file-extract-results-38.json', 'utf8'));

const nodes = [];
const edges = [];

function addNode(node) { nodes.push(node); }
function addEdge(src, tgt, type, weight) {
  edges.push({ source: src, target: tgt, type: type, direction: 'forward', weight: weight });
}

const fileMeta = {
  'src/Wavee.UI.WinUI/Converters/CachingProfileTooltipConverter.cs': {
    summary: 'WinUI IValueConverter that caches tooltip text derived from a Spotify user profile object to avoid redundant string formatting on repeated binding evaluations.',
    tags: ['utility', 'converter', 'component', 'winui'], complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Converters/ChipBrushConverters.cs': {
    summary: 'Provides two WinUI IValueConverters — ChipBackgroundConverter and ChipForegroundConverter — that map a genre/chip value to appropriate brush resources for chip-style UI elements.',
    tags: ['utility', 'converter', 'component', 'winui'], complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Converters/CountToVisibilityConverter.cs': {
    summary: 'WinUI IValueConverter that collapses a UI element when a bound integer count is zero, making lists or badges conditionally visible.',
    tags: ['utility', 'converter', 'component', 'winui'], complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Converters/DepthToThicknessConverter.cs': {
    summary: 'WinUI IValueConverter that converts a nesting depth integer to a Thickness for indentation in tree or hierarchical list UI layouts.',
    tags: ['utility', 'converter', 'component', 'winui'], complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Converters/GridScaleToSizeConverter.cs': {
    summary: 'WinUI IValueConverter that converts a grid scale enum or numeric factor to a pixel size value for adaptive grid layouts.',
    tags: ['utility', 'converter', 'component', 'winui'], complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Converters/InverseBoolConverter.cs': {
    summary: 'Simple WinUI IValueConverter that negates a boolean value, used to toggle visibility or enabled state in XAML bindings.',
    tags: ['utility', 'converter', 'component', 'winui'], complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Converters/NullableDoubleToDoubleConverter.cs': {
    summary: 'WinUI IValueConverter that converts a nullable double to a non-nullable double with a configurable fallback, enabling safe binding to slider and numeric controls.',
    tags: ['utility', 'converter', 'component', 'winui'], complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Converters/NullToBoolConverter.cs': {
    summary: 'WinUI IValueConverter that converts a null reference to a boolean, returning false for null and true for non-null to drive IsEnabled or IsChecked bindings.',
    tags: ['utility', 'converter', 'component', 'winui'], complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Converters/NullToForegroundConverter.cs': {
    summary: 'WinUI IValueConverter that selects a foreground brush based on whether a bound value is null, used to indicate placeholder versus populated state in text elements.',
    tags: ['utility', 'converter', 'component', 'winui'], complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Converters/NullToVisibilityConverter.cs': {
    summary: 'WinUI IValueConverter that collapses UI elements when a bound value is null, commonly used to hide sections that lack data.',
    tags: ['utility', 'converter', 'component', 'winui'], complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Converters/PlayerConverters.cs': {
    summary: 'Collection of WinUI IValueConverters for playback controls: glyph selection for play/pause and volume, repeat-mode glyph and checked state, milliseconds-to-time-string formatting, and URL-to-ImageSource conversion.',
    tags: ['utility', 'converter', 'component', 'winui'], complexity: 'moderate'
  },
  'src/Wavee.UI.WinUI/Converters/PlayerLocationToVisibilityConverter.cs': {
    summary: 'WinUI IValueConverter that converts a player location enum to a Visibility, used to show or hide player controls based on which panel owns the player.',
    tags: ['utility', 'converter', 'component', 'winui'], complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Converters/PlaylistLayoutModeToVisibilityConverter.cs': {
    summary: 'Provides two WinUI IValueConverters that toggle playlist header banner versus cover-image visibility based on the selected layout mode.',
    tags: ['utility', 'converter', 'component', 'winui'], complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Converters/SpotifyImageConverter.cs': {
    summary: 'WinUI IValueConverter that resolves a Spotify image identifier or URL to an ImageSource for album art and profile picture slots.',
    tags: ['utility', 'converter', 'component', 'winui'], complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Converters/StatusToActiveConverter.cs': {
    summary: 'WinUI IValueConverter that maps a status enum value to a boolean active state to drive highlighted appearance in status indicators.',
    tags: ['utility', 'converter', 'component', 'winui'], complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Converters/StringToUpperConverter.cs': {
    summary: 'WinUI IValueConverter that uppercases a string binding value for labels requiring uppercase per Fluent Design typography guidelines.',
    tags: ['utility', 'converter', 'component', 'winui'], complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Data/ChangelogData.cs': {
    summary: 'Static data class exposing an in-memory list of Releases for the application changelog, consumed by the About page without a network call.',
    tags: ['data-model', 'utility', 'component'], complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Data/Contexts/ActivityService.cs': {
    summary: 'Singleton service managing an observable list of in-app activity notifications with start/complete/fail/update lifecycle, category styling, and read/clear operations via CommunityToolkit messenger.',
    tags: ['service', 'event-handler', 'singleton', 'winui'], complexity: 'complex'
  },
  'src/Wavee.UI.WinUI/Data/Contexts/AlbumService.cs': {
    summary: 'Data access service for album detail and track listing, coordinating Pathfinder GraphQL queries, extended metadata, local library, and a hot-path LRU cache to serve album pages, merch, similar albums, and recommended playlists.',
    tags: ['service', 'api-handler', 'data-model', 'winui'], complexity: 'complex'
  },
  'src/Wavee.UI.WinUI/Data/Contexts/AppState.cs': {
    summary: 'Lightweight singleton holding top-level application state flags consumed by services that need to react to global state transitions.',
    tags: ['service', 'singleton', 'data-model', 'winui'], complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Data/Contexts/ArtistService.cs': {
    summary: 'Data access service for artist overview pages, orchestrating Pathfinder GraphQL for top tracks, discography, concerts, music videos, merch, playlists, and related artists with local library fallback.',
    tags: ['service', 'api-handler', 'data-model', 'winui'], complexity: 'complex'
  },
  'src/Wavee.UI.WinUI/Data/Contexts/AuthStateService.cs': {
    summary: 'Authentication state manager handling session restore, OAuth authorization-code and device-code login flows, logout, and user-profile population, broadcasting auth status via CommunityToolkit messenger.',
    tags: ['service', 'singleton', 'event-handler', 'winui'], complexity: 'complex'
  },
  'src/Wavee.UI.WinUI/Data/Contexts/ConnectCommandExecutor.cs': {
    summary: 'Central command executor for Spotify Connect operations, routing play/pause/seek/skip/shuffle/repeat/queue/transfer/audio-video-switch commands to the local audio engine or remote Connect devices.',
    tags: ['service', 'api-handler', 'event-handler', 'winui'], complexity: 'complex'
  },
  'src/Wavee.UI.WinUI/Data/Contexts/ConnectivityService.cs': {
    summary: 'Service that monitors Spotify session connection state changes and broadcasts connectivity status messages to the UI via CommunityToolkit messenger.',
    tags: ['service', 'event-handler', 'singleton', 'winui'], complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Data/Contexts/FriendsFeedService.cs': {
    summary: 'Real-time friends activity feed service that subscribes to Dealer presence push messages, fetches per-user friend presence via SpClient, and maintains an observable list of FriendFeedRowViewModels with watchdog reseeding and per-row tick refresh.',
    tags: ['service', 'event-handler', 'singleton', 'winui'], complexity: 'complex'
  }
};

const classSummaries = {
  'BoolToPlayPauseGlyphConverter': 'Converts a boolean playing state to the play or pause Fluent icon glyph for the transport bar.',
  'VolumeToGlyphConverter': 'Maps a volume level double to the appropriate muted/low/medium/high speaker glyph.',
  'RepeatModeToGlyphConverter': 'Selects the repeat-one or repeat-all glyph based on the current RepeatMode enum value.',
  'MillisecondsToTimeStringConverter': 'Formats a millisecond integer to a MM:SS or H:MM:SS time string for the seek bar.',
  'RepeatModeToCheckedConverter': 'Returns checked state for the repeat toggle button based on whether any repeat mode is active.',
  'RepeatModeToSymbolConverter': 'Converts a RepeatMode enum to the matching WinUI Symbol value for the repeat button icon.',
  'StringToImageSourceConverter': 'Converts a URL string to a BitmapImage ImageSource for inline image bindings in player and cards.',
  'ChipBackgroundConverter': 'Returns the background brush resource for a genre chip based on its value.',
  'ChipForegroundConverter': 'Returns the foreground brush resource for a genre chip based on its value.',
  'AlbumTrackResultJsonContext': 'AOT-safe System.Text.Json serialization context for album track result DTOs.',
  'ActivityService': 'Observable notification service managing in-app activity items with start/complete/fail/update lifecycle, category styling, and read/clear operations.',
  'AlbumService': 'Data service providing album detail, tracks, merch, similar albums, music videos, and recommended playlists via Pathfinder and extended metadata clients.',
  'ArtistService': 'Data service orchestrating artist overview pages including top tracks, discography, concerts, music videos, merch, playlists, and related artists.',
  'AuthStateService': 'Manages Spotify authentication state with session restore, OAuth flows, user profile population, and status broadcasting.',
  'ConnectCommandExecutor': 'Routes all Spotify Connect playback commands to the local audio engine or remote devices, covering play/pause/seek/skip/shuffle/repeat/queue/transfer.',
  'FriendsFeedService': 'Subscribes to Dealer presence pushes to maintain a live observable list of friends activity rows with watchdog reseeding and per-row tick refresh.',
  'ChangelogData': 'Static holder for the in-memory application changelog release list.',
  'AppState': 'Singleton container for top-level app state flags consumed across services.',
  'ConnectivityService': 'Monitors session connection state and broadcasts connectivity change messages.',
  'CachingProfileTooltipConverter': 'Caches computed tooltip text from Spotify user profile data to reduce redundant string formatting.',
  'CountToVisibilityConverter': 'Collapses UI elements when a bound integer count is zero for conditional visibility.',
  'DepthToThicknessConverter': 'Converts a nesting depth integer to a Thickness for indentation in hierarchical list layouts.',
  'GridScaleToSizeConverter': 'Converts a grid scale factor to a pixel size for adaptive grid layouts.',
  'InverseBoolConverter': 'Negates a boolean binding value for toggling visibility or enabled state.',
  'NullableDoubleToDoubleConverter': 'Converts a nullable double to a non-nullable double with a configurable fallback.',
  'NullToBoolConverter': 'Maps null to false and non-null to true for IsEnabled or IsChecked bindings.',
  'NullToForegroundConverter': 'Selects a foreground brush based on whether a bound value is null.',
  'NullToVisibilityConverter': 'Collapses UI elements when a bound value is null.',
  'PlayerLocationToVisibilityConverter': 'Shows or hides player controls based on which panel owns the player.',
  'PlaylistLayoutModeBannerVisibilityConverter': 'Toggles playlist header banner visibility based on the selected layout mode.',
  'PlaylistLayoutModeCoverVisibilityConverter': 'Toggles playlist cover-image visibility based on the selected layout mode.',
  'SpotifyImageConverter': 'Resolves a Spotify image identifier to an ImageSource for display.',
  'StatusToActiveConverter': 'Maps a status enum to a boolean active state for status indicator styling.',
  'StringToUpperConverter': 'Uppercases a string binding value for typography-compliant label rendering.'
};

const fnSummaries = {
  'ActivityService': 'Constructor wiring messenger handlers for auth status changes and registering category styles.',
  'Post': 'Creates and dispatches a new activity notification item with optional actions, icon, status, and message.',
  'Start': 'Posts an indeterminate-progress item and returns its ID for lifecycle tracking.',
  'Complete': 'Marks an activity item as completed and updates its display message.',
  'Fail': 'Marks an activity item as failed with an error message.',
  'Update': 'Updates progress, message, progressText, and ETA for an in-flight activity item.',
  'AddItem': 'Inserts an activity item into the observable list and triggers unread-count update.',
  'Remove': 'Removes an activity item by ID from the observable list.',
  'GetTracksAsync': 'Fetches the full track list for an album URI, merging Pathfinder data with local library metadata.',
  'GetDetailAsync': 'Retrieves full album detail including metadata, palette, tier, and header images with local library fallback.',
  'MapTier': 'Maps a visual identity palette to the album tier classification for header styling.',
  'MapPalette': 'Converts a Pathfinder visual identity object to a serializable color palette record.',
  'GetMerchAsync': 'Fetches merchandise items for an album URI via extended metadata client.',
  'GetSimilarAlbumsAsync': 'Retrieves similar albums for a given track URI using Pathfinder recommendations.',
  'GetMusicVideoUriAsync': 'Resolves the Spotify video URI for a track if a music video exists.',
  'GetSingleTrackContextAsync': 'Builds a single-track playback context DTO for play-on-click from an album track row.',
  'GetArtistNpvAsync': 'Fetches the now-playing view artist data for a given artist URI.',
  'GetArtistContextAsync': 'Retrieves the artist context header data used in the album page artist section.',
  'SynthesizeAlbumDetailResult': 'Assembles an AlbumDetailResult by merging Pathfinder and local library data into the final DTO.',
  'MapLocalTrackToAlbumTrack': 'Converts a local library track entity to the AlbumTrack DTO.',
  'ToDto': 'Maps a raw Pathfinder album track to the AlbumTrackDto consumed by the view model.',
  'MapTracksRaw': 'Processes the raw track list from Pathfinder applying disc grouping and track number normalization.',
  'MapTrackArtists': 'Builds the list of artist reference DTOs from a raw Pathfinder track artists collection.',
  'FetchFromApiAsync': 'Executes the Pathfinder GraphQL album query and returns the raw response envelope.',
  'GetRecommendedPlaylistsAsync': 'Fetches editorially curated playlists recommended in the context of an album.',
  'GetOverviewAsync': 'Fetches the full artist overview from Pathfinder including header, top tracks, discography, concerts, music videos, merch, playlists, and related artists.',
  'PrewarmMusicVideoCatalog': 'Seeds the music video title-to-URI mapping dictionary from a fresh discography response.',
  'MapConcertsAsync': 'Converts raw Pathfinder concert data to ConcertDto enriched with venue and geo-location info.',
  'GetDiscographyAllAsync': 'Fetches a paginated slice of all discography releases for an artist URI.',
  'GetDiscographyPageAsync': 'Fetches a typed discography page (albums/singles/compilations) with offset and limit.',
  'MapTopTracks': 'Converts Pathfinder top-track objects to ArtistTopTrackDto with playcount and artist refs.',
  'MapMusicVideoMappings': 'Extracts a title-to-video-URI dictionary from the artist discography for video badge display.',
  'AddMappings': 'Merges new title-to-video-URI pairs into the existing mapping dictionary.',
  'GetExtendedTopTracksAsync': 'Fetches enriched track details for top tracks that need extended metadata.',
  'GetTrackImagesAsync': 'Resolves artwork URLs for top tracks from local cache or extended metadata.',
  'MapReleaseGroup': 'Maps a raw release-group record from Pathfinder to an ArtistReleaseDto.',
  'MapPopularReleases': 'Selects and maps the popular release section entries for the artist overview.',
  'MapMusicVideos': 'Converts music video entries to ArtistMusicVideoDto with matched URI lookups.',
  'AppendPlaylists': 'Adds a paginated block of playlists to the running artist overview playlists list.',
  'MapMerch': 'Converts raw merch items to ArtistMerchDto for the merch shelf section.',
  'MapRelatedArtists': 'Maps related artist references from Pathfinder to RelatedArtistDto.',
  'MapPinnedItem': 'Converts a pinned item (album/playlist) from the artist profile to the pinned item DTO.',
  'SynthesizeArtistOverviewResult': 'Combines all fetched sections into the final ArtistOverviewResult DTO returned to the view model.',
  'MapLocalTrackToArtistTopTrack': 'Converts a local library track to ArtistTopTrackDto for artists with local files.',
  'MapLocalAlbumSummaryToRelease': 'Converts a local library album summary to ArtistReleaseDto for the discography list.',
  'TryRestoreSessionAsync': 'Attempts to restore a previous session from cached credentials and populate user data.',
  'LoginWithAuthorizationCodeAsync': 'Runs the OAuth authorization-code flow, opening a browser and waiting for the redirect callback.',
  'LoginWithDeviceCodeAsync': 'Executes the OAuth device-code flow, displaying the code and polling for authorization.',
  'LogoutAsync': 'Clears cached credentials, resets session state, and broadcasts logout completion.',
  'PopulateUserFromSession': 'Fetches and stores user profile, account type, and country code after a successful session connect.',
  'SetEqualizerAsync': 'Applies a new equalizer band-gain configuration to the local audio pipeline.',
  'ToPlaybackResult': 'Converts an internal command result enum to the public PlaybackResult DTO.',
  'ShouldBlockLocalSpotifyPlayback': 'Determines whether local Spotify audio playback should be suppressed given current device and cluster state.',
  'TryFindCurrentSpotifyAudioUri': 'Resolves the currently active Spotify audio URI from local cluster or Connect state.',
  'TryFindSpotifyAudioUri': 'Searches queue and context buckets for a Spotify audio URI matching a given item.',
  'SendAsync': 'Core dispatch method routing a playback command to the local engine or a remote Connect device with ACK waiting.',
  'GetAckTimeout': 'Returns the ACK timeout for a command type based on network latency expectations.',
  'ShouldWaitForAck': 'Returns whether the executor should await a Connect ACK for the given command type.',
  'SwitchToContextAfterCurrentAsync': 'Queues a context switch to occur after the currently playing track completes.',
  'PlayContextAsync': 'Initiates playback of a URI context (album/playlist/artist) at an optional offset via Connect.',
  'PlayTracksAsync': 'Starts playback of an explicit list of track URIs, building a synthetic context and queue.',
  'PlayOriginFeatureFor': 'Returns the play-origin feature string for a given playback context URI type.',
  'PlayOriginFeatureForUri': 'Returns the play-origin feature string for a specific item URI within a context.',
  'SetRepeatAsync': 'Sets the repeat mode (off/context/track) on the local engine or remote device.',
  'SetPlaybackSpeedAsync': 'Adjusts the playback speed for the current track (podcast/audiobook contexts).',
  'ReorderQueueAsync': 'Reorders items in the user queue by moving a range to a new position.',
  'SkipToQueueItemAsync': 'Skips playback to a specific item in the queue by its queue-position index.',
  'SendQueueMutationAsync': 'Executes a queue mutation with ACK waiting and cluster sync.',
  'BuildSetQueueBody': 'Constructs the SetQueue protobuf payload from current queue state with a new ordering.',
  'RouteToLocalEngineAsync': 'Handles all commands directed at the local audio engine, translating Connect command types to AudioEngine API calls.',
  'BuildPlayCommand': 'Constructs the Connect Play command protobuf for a context or track list playback request.',
  'TransferPlaybackAsync': 'Transfers active playback to a different Connect device, optionally preserving current position.',
  'SwitchAudioOutputAsync': 'Changes the local audio output device and reconnects the audio pipeline.',
  'SwitchToVideoAsync': 'Switches the current track from audio to the music video rendition if available.',
  'SwitchToAudioAsync': 'Switches back from a music video rendition to the standard audio track.',
  'OnConnectionStateChanged': 'Handles session connection state changes and broadcasts ConnectionStatusChangedMessage.',
  'FriendsFeedService': 'Constructor wiring auth status reception, initializing the observable collection, and starting Dealer subscription.',
  'Receive': 'Handles CommunityToolkit messenger messages to subscribe or tear down on auth state changes.',
  'TearDown': 'Cancels all in-flight requests, disposes Dealer subscriptions, and clears the friends list on logout.',
  'TrySubscribeToDealer': 'Subscribes to Dealer presence push messages and connection-ID changes to drive friend feed updates.',
  'OnConnectionIdChanged': 'Reacts to Dealer connection ID changes by cancelling pending work and reseeding the friend feed.',
  'CancelAllUserFetches': 'Cancels and disposes all per-user presence-fetch CancellationTokenSources.',
  'OnDealerPush': 'Handles incoming presence push messages by dispatching async per-user upsert operations.',
  'FetchAndUpsertUserAsync': 'Fetches friend presence data from SpClient for a single user and upserts or removes their row.',
  'UpsertRow': 'Inserts or replaces a FriendFeedRowViewModel at the correct sorted position in the observable list.',
  'RemoveRowForUser': 'Removes the row for a given user URI from the observable list.',
  'SetActive': 'Activates or deactivates the feed, starting or stopping the watchdog and row-tick timers.',
  'RefreshAsync': 'Forces a manual reseed of the friend feed from the SpClient API.',
  'OnSafetyTick': 'Watchdog timer callback that reseeds the feed if no Dealer push has been received within the interval.',
  'SeedAsync': 'Fetches the full friend feed snapshot from SpClient and applies it to the observable list.',
  'ApplySnapshot': 'Replaces the observable list contents with a new sorted snapshot from a SpClient feed response.',
  'Dispose': 'Disposes all timers, cancels in-flight work, and releases Dealer subscriptions.',
  'OnRowTick': 'Per-row timer callback that refreshes relative timestamps on all visible feed rows.',
  'AppState': 'Constructor or init for the AppState singleton.',
  'Convert': 'Converts the bound value to the target type for XAML binding evaluation.',
  'ConnectCommandExecutor': 'Constructor accepting Connect client, session, and logger dependencies.'
};

// Process all files
data.results.forEach(r => {
  const meta = fileMeta[r.path];
  const fileId = 'file:' + r.path;

  addNode({
    id: fileId, type: 'file', name: r.path.split('/').pop(),
    filePath: r.path, summary: meta.summary, tags: meta.tags, complexity: meta.complexity
  });

  // Class nodes
  (r.classes || []).forEach(c => {
    const lineLen = c.endLine - c.startLine;
    const methodCount = c.methods ? c.methods.length : 0;
    if (methodCount >= 2 || lineLen >= 20) {
      const classId = 'class:' + r.path + ':' + c.name;
      const summary = classSummaries[c.name] || c.name + ' class.';
      const isConverterFile = r.path.includes('Converters/');
      const tags = isConverterFile ? ['utility', 'converter', 'component'] :
        c.name.endsWith('Service') ? ['service', 'singleton', 'event-handler'] :
        c.name === 'ChangelogData' ? ['data-model', 'utility'] :
        c.name === 'AppState' ? ['data-model', 'singleton'] :
        ['component', 'utility'];
      const complexity = lineLen < 50 ? 'simple' : lineLen < 200 ? 'moderate' : 'complex';
      addNode({ id: classId, type: 'class', name: c.name, filePath: r.path, lineRange: [c.startLine, c.endLine], summary, tags, complexity });
      addEdge(fileId, classId, 'contains', 1.0);
    }
  });

  // Function nodes (10+ lines only)
  const fnNames = new Set();
  (r.functions || []).forEach(f => {
    const lineLen = f.endLine - f.startLine;
    if (lineLen >= 10) {
      const uniqueName = fnNames.has(f.name) ? f.name + '_L' + f.startLine : f.name;
      fnNames.add(f.name);
      const fnId = 'function:' + r.path + ':' + uniqueName;
      const summary = fnSummaries[f.name] || f.name + ' method.';
      const isConverterFile = r.path.includes('Converters/');
      const tags = isConverterFile ? ['utility', 'converter'] :
        f.name.endsWith('Async') ? ['service', 'api-handler'] :
        (f.name.startsWith('Map') || f.name.startsWith('Parse') || f.name.startsWith('Synthesize')) ? ['utility', 'data-model'] :
        f.name.startsWith('On') ? ['event-handler', 'service'] : ['service', 'utility'];
      const complexity = lineLen < 30 ? 'simple' : lineLen < 100 ? 'moderate' : 'complex';
      addNode({ id: fnId, type: 'function', name: f.name, filePath: r.path, lineRange: [f.startLine, f.endLine], summary, tags, complexity });
      addEdge(fileId, fnId, 'contains', 1.0);
    }
  });
});

// Import edge for FriendsFeedService -> Session
addEdge('file:src/Wavee.UI.WinUI/Data/Contexts/FriendsFeedService.cs', 'file:src/Wavee/Core/Session/Session.cs', 'imports', 0.7);

// Calls edges from FriendsFeedService to Session symbols
addEdge('file:src/Wavee.UI.WinUI/Data/Contexts/FriendsFeedService.cs', 'function:src/Wavee/Core/Session/Session.cs:SpClient', 'calls', 0.8);
addEdge('file:src/Wavee.UI.WinUI/Data/Contexts/FriendsFeedService.cs', 'function:src/Wavee/Core/Session/Session.cs:Dealer', 'calls', 0.8);

console.log('Total nodes:', nodes.length);
console.log('Total edges:', edges.length);

// Split into parts if needed
// Group nodes by their filePath, then bin files into parts such that no part exceeds 60 nodes.
// Files sorted alphabetically per spec; each file's nodes travel together.
const allFilePaths = [...new Set(nodes.map(n => n.filePath).filter(Boolean))].sort();

// Count nodes per file
const nodesByFile = {};
allFilePaths.forEach(fp => { nodesByFile[fp] = nodes.filter(n => n.filePath === fp); });

// Greedily pack files into parts of <=60 nodes
const fileParts = []; // array of Set<filePath>
let current = new Set();
let currentCount = 0;
for (const fp of allFilePaths) {
  const count = nodesByFile[fp].length;
  if (currentCount + count > 60 && current.size > 0) {
    fileParts.push(current);
    current = new Set();
    currentCount = 0;
  }
  current.add(fp);
  currentCount += count;
}
if (current.size > 0) fileParts.push(current);

console.log('Parts needed:', fileParts.length);

for (let p = 0; p < fileParts.length; p++) {
  const chunkPaths = fileParts[p];
  const partNodes = nodes.filter(n => n.filePath && chunkPaths.has(n.filePath));
  const partNodeIds = new Set(partNodes.map(n => n.id));
  const partEdges = edges.filter(e => partNodeIds.has(e.source));
  const output = { nodes: partNodes, edges: partEdges };
  const outPath = 'C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-38-part-' + (p + 1) + '.json';
  fs.writeFileSync(outPath, JSON.stringify(output));
  console.log('Part', p+1, ':', partNodes.length, 'nodes,', partEdges.length, 'edges ->', outPath);
}
