const fs = require('fs');
const data = JSON.parse(fs.readFileSync('C:/WAVEE/WaveeMusic/.understand-anything/tmp/ua-file-extract-results-44.json', 'utf8'));

function isExported(r, fnName) {
  return r.exports && r.exports.some(e => e.name === fnName);
}
function fnLines(f) { return (f.endLine || 0) - (f.startLine || 0) + 1; }
function clLines(c) { return (c.endLine || 0) - (c.startLine || 0) + 1; }

const fileMeta = {
  'src/Wavee.UI.WinUI/Services/DispatcherService.cs': {
    summary: 'Thin WinUI dispatcher wrapper that routes UI-thread actions through DispatcherQueue, implementing IDispatcherService.',
    tags: ['service', 'utility', 'dispatcher', 'winui'],
    complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Services/Docking/DetachablePanel.cs': {
    summary: 'Enum or record defining which panels (Player, RightPanel) can be detached from the main window into floating windows.',
    tags: ['data-model', 'docking', 'winui'],
    complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Services/Docking/IPanelDockingService.cs': {
    summary: 'Interface contract for detaching, docking, and rehydrating floating panel windows (Player, RightPanel) with geometry persistence.',
    tags: ['service', 'docking', 'type-definition', 'winui'],
    complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Services/Docking/PanelDockingService.cs': {
    summary: 'Implements panel detach/dock lifecycle: creates AppWindows for Player and RightPanel, persists geometry via AppSettings, and clamps placement to visible monitors.',
    tags: ['service', 'docking', 'winui', 'window-management'],
    complexity: 'complex'
  },
  'src/Wavee.UI.WinUI/Services/DpapiTmdbTokenStore.cs': {
    summary: 'Persists and retrieves a TMDB API token encrypted with Windows DPAPI, exposing reactive HasToken and HasTokenChanged observables.',
    tags: ['service', 'security', 'data-model', 'reactive'],
    complexity: 'complex'
  },
  'src/Wavee.UI.WinUI/Services/FeedbackService.cs': {
    summary: 'Submits user feedback reports via HTTP POST to the Wavee feedback endpoint.',
    tags: ['service', 'api-handler', 'feedback'],
    complexity: 'moderate'
  },
  'src/Wavee.UI.WinUI/Services/HeroSlideFactory.cs': {
    summary: 'Factory that maps raw Home API items into typed HeroSlide view-model objects, including accent color parsing and image URI resolution.',
    tags: ['factory', 'home-feed', 'hero-slide', 'ui'],
    complexity: 'moderate'
  },
  'src/Wavee.UI.WinUI/Services/HomeFeedCache.cs': {
    summary: 'Smart diffing cache for the Home feed that fetches, diffs, and incrementally applies section/item updates to avoid full re-renders, with chunked async apply for large changesets.',
    tags: ['service', 'home-feed', 'cache', 'diffing'],
    complexity: 'complex'
  },
  'src/Wavee.UI.WinUI/Services/HomeRawJsonHelper.cs': {
    summary: 'Static helper that extracts the raw JSON for a specific section index from a Home API response for debug and enrichment purposes.',
    tags: ['utility', 'home-feed', 'json'],
    complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Services/HomeResponseParserFactory.cs': {
    summary: 'Selects the correct Home response parser (V1 or V2) based on response shape, then merges baseline sections from both versions.',
    tags: ['factory', 'home-feed', 'parser'],
    complexity: 'complex'
  },
  'src/Wavee.UI.WinUI/Services/HomeResponseParserV1.cs': {
    summary: 'Parses the legacy (V1) Spotify Home API JSON format into typed section and item view-models, mapping artists, albums, playlists, podcasts, and episodes.',
    tags: ['service', 'home-feed', 'parser', 'serialization'],
    complexity: 'complex'
  },
  'src/Wavee.UI.WinUI/Services/HomeResponseParserV2.cs': {
    summary: 'Parses the current (V2) Spotify Home GraphQL response format with entity-typed payloads, protobuf-like varint decoding for group metadata, and multi-image extraction.',
    tags: ['service', 'home-feed', 'parser', 'serialization'],
    complexity: 'complex',
    languageNotes: 'Implements a hand-rolled varint decoder (TryReadVarint) for decoding protobuf-encoded group metadata embedded in Home API responses.'
  },
  'src/Wavee.UI.WinUI/Services/IActiveVideoSurfaceService.cs': {
    summary: 'Interface for managing exclusive ownership of the active video composition surface, allowing providers to register, acquire, and release the surface.',
    tags: ['service', 'type-definition', 'video', 'winui'],
    complexity: 'moderate'
  },
  'src/Wavee.UI.WinUI/Services/IHomeFeedCache.cs': {
    summary: 'Interface contract for the Home feed cache: cache read, fresh fetch, invalidation, background refresh control, and incremental diff application.',
    tags: ['service', 'type-definition', 'home-feed', 'cache'],
    complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Services/ImageCacheCleanupAdapter.cs': {
    summary: 'Adapter implementing ICacheCleanupService to bridge ImageCacheService into the shared cache cleanup infrastructure.',
    tags: ['service', 'cache', 'adapter', 'image'],
    complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Services/ImageCacheService.cs': {
    summary: 'In-memory LRU image cache with pinning support, size-budget enforcement, bucket-snapped decode sizes, and stale-entry cleanup for CompositionImage consumers.',
    tags: ['service', 'cache', 'image', 'lru'],
    complexity: 'complex',
    languageNotes: 'Uses an intrusive doubly-linked LRU list with a separate pin-count to prevent eviction of currently-rendered images.'
  },
  'src/Wavee.UI.WinUI/Services/ImageLoadingSuspension.cs': {
    summary: 'Singleton gate controlling whether image loads are suspended during navigation transitions, exposing a reactive IsSuspended observable.',
    tags: ['service', 'image', 'reactive', 'singleton'],
    complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Services/IMediaSurfaceConsumer.cs': {
    summary: 'Interface for UI controls that consume a WinUI/Composition media surface, supporting attach, element-surface attach, and detach operations.',
    tags: ['service', 'type-definition', 'video', 'winui'],
    complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Services/InMemoryLoggerProvider.cs': {
    summary: 'Serilog sink that buffers structured log entries in an ObservableCollection with dispatcher-thread marshalling, used by the in-app Debug log viewer.',
    tags: ['service', 'logging', 'debug', 'serilog'],
    complexity: 'complex'
  },
  'src/Wavee.UI.WinUI/Services/INowPlayingPresentationService.cs': {
    summary: 'Interface for controlling Now Playing presentation modes: normal, theatre, and fullscreen, with toggle helpers.',
    tags: ['service', 'type-definition', 'now-playing', 'ui'],
    complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Services/InPageFilterController.cs': {
    summary: 'Observable filter controller that exposes query/field state and notifies the active filterable page when the user requests a filter operation.',
    tags: ['service', 'filtering', 'reactive', 'ui'],
    complexity: 'complex'
  },
  'src/Wavee.UI.WinUI/Services/IProfileCache.cs': {
    summary: 'Interface for the user profile cache: cached read, fresh fetch, invalidation, clear, and background refresh lifecycle.',
    tags: ['service', 'type-definition', 'profile', 'cache'],
    complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Services/ISharedCardCanvasPreviewService.cs': {
    summary: 'Interface and lease record for the shared CanvasDevice preview service used by ContentCard animated canvas previews.',
    tags: ['service', 'type-definition', 'card', 'canvas'],
    complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Services/ISpotifyVideoPlaybackDetails.cs': {
    summary: 'Interface for querying and selecting video quality for Spotify video playback tracks.',
    tags: ['service', 'type-definition', 'video', 'playback'],
    complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Services/IUpdateService.cs': {
    summary: 'Interface for the app update service, providing an async check-for-update method returning available version info.',
    tags: ['service', 'type-definition', 'update'],
    complexity: 'simple'
  }
};

// Function summaries for significant functions
function getFnSummary(r, f) {
  const className = r.classes && r.classes[0] ? r.classes[0].name : '';
  const summaries = {
    'PanelDockingService': {
      'Detach': 'Creates a floating AppWindow for the given panel at the specified screen position, persisting its geometry.',
      'Dock': 'Returns a floating panel back into the main window and closes its AppWindow.',
      'NotifyFloatingGeometryChanged': 'Persists the new position/size when a floating panel window is moved or resized.',
      'RehydrateAsync': 'On startup, re-creates floating windows for any panels that were detached in the previous session.',
      'ApplyInitialPlacement': 'Positions a new floating window at the requested spawn point, adjusting for title-bar height.',
      'ClampToVisibleMonitor': 'Clamps a window rect to ensure it remains visible on any connected monitor.'
    },
    'DpapiTmdbTokenStore': {
      'GetTokenAsync': 'Reads and DPAPI-decrypts the persisted TMDB token from the local settings store.',
      'SetTokenAsync': 'DPAPI-encrypts and persists a new TMDB token, updating the reactive HasToken state.'
    },
    'HomeFeedCache': {
      'FetchCoreAsync': 'Fetches a fresh Home feed from the API and runs the full diff-and-apply pipeline.',
      'ApplyDiff': 'Synchronously diffs fresh sections against the current snapshot and updates observables in-place.',
      'ApplyDiffChunkedAsync': 'Asynchronously applies a large feed diff in section-sized chunks to avoid blocking the UI thread.',
      'UpdateSectionInPlace': 'Updates a single section node in-place, diffing its item list and notifying accent-change callbacks.',
      'DiffItems': 'Computes a three-way diff (added, removed, updated) between the current and fresh item lists.',
      'UpdateItemInPlace': 'Updates individual fields of a Home feed item without replacing the object, preserving bindings.',
      'FillMissingDisplayData': 'Fills in null display fields (title, subtitle, image) on a fresh item from the previous snapshot.',
      'RemoveUnrenderableItems': 'Strips items that lack the minimum required data to render a card from the feed.',
      'IsUnrenderable': 'Returns true when an item has no navigable URI and no display data, making it unrenderable.'
    },
    'HomeResponseParserFactory': {
      'Parse': 'Detects the Home response format (V1 or V2), delegates to the appropriate parser, and merges baseline sections.',
      'CombineBaselineSections': 'Merges sections parsed by V1 and V2 parsers, deduplicating by section ID.'
    },
    'HomeResponseParserV1': {
      'MapSections': 'Iterates V1 response section entries and maps each to a typed HomeSectionViewModel.',
      'MapSectionItem': 'Dispatches a single V1 entry to the correct type mapper (artist, album, playlist, etc.).',
      'MapArtist': 'Maps a V1 artist entry to an ArtistContentItem with image and subtitle.',
      'MapPlaylist': 'Maps a V1 playlist entry to a PlaylistContentItem with owner and image.',
      'MapAlbum': 'Maps a V1 album entry to an AlbumContentItem with artists and release year.',
      'MapPodcast': 'Maps a V1 podcast entry to a PodcastContentItem with description and image.',
      'MapEpisode': 'Maps a V1 episode entry to an EpisodeContentItem with progress and played state.',
      'EnrichFromRawJson': 'Adds missing image/color data to an already-mapped item using the raw section JSON.',
      'ExtractImageUrlFromJson': 'Extracts the best-quality image URL from a V1 raw JSON image block.',
      'GetLargestSourceUrl': 'Picks the largest source URL from a V1 image sources container.',
      'ExtractColorFromJson': 'Parses the accent/background color from a V1 raw JSON color block.',
      'MapUnknownType': 'Creates a fallback UnknownContentItem for unrecognized V1 URI types.'
    },
    'HomeResponseParserV2': {
      'MapSections': 'Iterates V2 response section entities and maps each to a typed HomeSectionViewModel.',
      'UnwrapListItems': 'Flattens nested list-item containers from a V2 section into a flat entry sequence.',
      'MapEntityItem': 'Maps a V2 typed entity (artist, album, playlist, etc.) to the appropriate ContentItem.',
      'MapV1ContentItem': 'Maps a legacy V1-format content item embedded inside a V2 response.',
      'MapV1Episode': 'Maps a V2 episode entity to an EpisodeContentItem with played state.',
      'MapV1Album': 'Maps a V2 album entity to an AlbumContentItem.',
      'MapV1Artist': 'Maps a V2 artist entity to an ArtistContentItem.',
      'MapV1Playlist': 'Maps a V2 playlist entity to a PlaylistContentItem.',
      'MapV1Podcast': 'Maps a V2 podcast entity to a PodcastContentItem.',
      'MapLikedSongs': 'Maps the special Liked Songs entity including group track count decoded from protobuf metadata.',
      'DecodeGroupMetadata': 'Decodes a protobuf-like binary blob carrying liked-songs group size and track URIs.',
      'TryReadVarint': 'Reads a single protobuf varint from a byte span at a given position.',
      'ResolveContentType': 'Infers the ContentType from a V2 typed entity data payload.',
      'ExtractImageUrls': 'Extracts all available image URLs from a V2 cover image entity.',
      'ExtractImageUrlFromJson': 'Extracts image URL from a V2 raw JSON payload with fallback strategies.',
      'ExtractSquareCoverImageUrl': 'Extracts the best URL from a V2 square cover image block.',
      'ExtractLargestSourceUrl': 'Picks the largest source URL from a V2 image source container.',
      'BuildSubtitle': 'Constructs the card subtitle string from entity data, contributor names, and content type.',
      'GetContributorNames': 'Extracts and formats the contributor display names from a V2 entity.'
    },
    'ImageCacheService': {
      'GetOrCreate': 'Looks up or creates a cached image surface entry, loading from URI if absent.',
      'TryGet': 'Returns a cached image entry without creating it, or null if not cached.',
      'Pin': 'Increments the pin count for a cache entry, preventing eviction while pinned.',
      'Unpin': 'Decrements the pin count for a cache entry, allowing eviction if the count reaches zero.',
      'TrimToCapacityNoLock': 'Evicts least-recently-used unpinned entries until the cache is within its size budget.',
      'Invalidate': 'Removes a specific URI/size entry from the cache, releasing its surface.',
      'CleanupStale': 'Removes cache entries that have not been accessed within the specified max age.',
      'Clear': 'Removes all entries from the cache, releasing all surfaces.',
      'ClearUnpinned': 'Removes all unpinned entries from the cache.'
    },
    'InMemorySink': {
      'Emit': 'Receives a Serilog LogEvent, formats it, and enqueues it for dispatcher-thread addition to the entry collection.',
      'Clear': 'Clears all buffered log entries and resets the pending queue.',
      'FlushPendingEntries': 'Drains the pending entry queue onto the dispatcher thread into the observable collection.'
    },
    'InPageFilterController': {
      'OnPageChanged': 'Updates the active filterable page target when navigation changes the current view.',
      'RequestFilter': 'Triggers the active page to apply the current query/field filter.',
      'SetField': 'Updates a filter field value and raises property-changed notification.'
    },
    'FeedbackService': {
      'SubmitAsync': 'POSTs a feedback request payload to the remote feedback API endpoint.'
    }
  };
  if (summaries[className] && summaries[className][f.name]) {
    return summaries[className][f.name];
  }
  return f.name + ' — method in ' + className + '.';
}

function getFnTags(f, r) {
  const lineCount = fnLines(f);
  if (f.name.toLowerCase().includes('async') || (f.params && f.params.includes('ct'))) {
    return ['async', 'method', 'service'];
  }
  if (f.name.startsWith('Map') || f.name.startsWith('Parse')) return ['serialization', 'parser', 'method'];
  if (f.name.startsWith('Extract') || f.name.startsWith('Get')) return ['utility', 'method'];
  return ['method', 'service'];
}

const nodes = [];
const edges = [];

data.results.forEach(r => {
  const meta = fileMeta[r.path] || {
    summary: r.path.split('/').pop() + ' — service file.',
    tags: ['service'],
    complexity: 'simple'
  };

  const fileNode = {
    id: 'file:' + r.path,
    type: 'file',
    name: r.path.split('/').pop(),
    filePath: r.path,
    summary: meta.summary,
    tags: meta.tags,
    complexity: meta.complexity
  };
  if (meta.languageNotes) fileNode.languageNotes = meta.languageNotes;
  nodes.push(fileNode);

  if (r.classes) {
    r.classes.forEach(c => {
      const methodCount = (c.methods || []).length;
      const lineCount = clLines(c);
      if (methodCount >= 2 || lineCount >= 20) {
        const isInterface = c.name.startsWith('I') && c.name.length > 1 && /[A-Z]/.test(c.name[1]);
        const classNode = {
          id: 'class:' + r.path + ':' + c.name,
          type: 'class',
          name: c.name,
          filePath: r.path,
          lineRange: [c.startLine, c.endLine],
          summary: isInterface
            ? 'Interface ' + c.name + ' defining the contract for ' + c.name.slice(1).replace(/([A-Z])/g, ' $1').trim().toLowerCase() + '.'
            : 'Implementation class ' + c.name + ' providing ' + c.name.replace(/([A-Z])/g, ' $1').trim().toLowerCase() + ' functionality.',
          tags: isInterface ? ['type-definition', 'service', 'interface'] : ['service', 'implementation'],
          complexity: lineCount > 200 ? 'complex' : (lineCount > 50 ? 'moderate' : 'simple')
        };
        nodes.push(classNode);
        edges.push({ source: 'file:' + r.path, target: 'class:' + r.path + ':' + c.name, type: 'contains', direction: 'forward', weight: 1.0 });
        edges.push({ source: 'file:' + r.path, target: 'class:' + r.path + ':' + c.name, type: 'exports', direction: 'forward', weight: 0.8 });
      }
    });
  }

  if (r.functions) {
    r.functions.forEach(f => {
      const lineCount = fnLines(f);
      const exported = isExported(r, f.name);
      if (lineCount >= 10 || exported) {
        const fnNode = {
          id: 'function:' + r.path + ':' + f.name,
          type: 'function',
          name: f.name,
          filePath: r.path,
          lineRange: [f.startLine, f.endLine],
          summary: getFnSummary(r, f),
          tags: getFnTags(f, r),
          complexity: lineCount > 50 ? 'complex' : (lineCount > 20 ? 'moderate' : 'simple')
        };
        nodes.push(fnNode);
        edges.push({ source: 'file:' + r.path, target: 'function:' + r.path + ':' + f.name, type: 'contains', direction: 'forward', weight: 1.0 });
        if (exported) {
          edges.push({ source: 'file:' + r.path, target: 'function:' + r.path + ':' + f.name, type: 'exports', direction: 'forward', weight: 0.8 });
        }
      }
    });
  }
});

console.log('Total nodes:', nodes.length, 'Total edges:', edges.length);
fs.writeFileSync('C:/WAVEE/WaveeMusic/.understand-anything/tmp/batch44-graph.json', JSON.stringify({ nodes, edges }), { encoding: 'utf8' });
console.log('Graph written.');
