const fs = require('fs');
const data = JSON.parse(fs.readFileSync('C:/WAVEE/WaveeMusic/.understand-anything/tmp/ua-file-extract-results-46.json', 'utf8'));

const nodes = [];
const edges = [];

function isSignificantFunction(f) {
  return (f.endLine - f.startLine + 1) >= 10;
}

function isSignificantClass(c) {
  return (c.methods && c.methods.length >= 2) || ((c.endLine - c.startLine + 1) >= 20);
}

const fileMeta = {
  'src/Wavee.UI.WinUI/Services/MusicVideos/MusicVideoCatalogCache.cs': {
    summary: 'In-memory thread-safe cache for music video availability, mapping track URIs to video/audio URIs and PlayReady manifest IDs.',
    tags: ['service', 'cache', 'music-video'],
    complexity: 'moderate'
  },
  'src/Wavee.UI.WinUI/Services/MusicVideos/MusicVideoDiscoveryService.cs': {
    summary: 'Background discovery service that probes track URIs for associated music videos and populates the video catalog cache via NPV mapping and manifest resolution.',
    tags: ['service', 'music-video', 'background-worker'],
    complexity: 'complex'
  },
  'src/Wavee.UI.WinUI/Services/MusicVideos/MusicVideoMetadataService.cs': {
    summary: 'Orchestrates music video metadata resolution — checking availability, resolving video and audio URIs via extended metadata, and handling local video file discovery.',
    tags: ['service', 'music-video', 'metadata'],
    complexity: 'complex'
  },
  'src/Wavee.UI.WinUI/Services/NavigationGcCoordinator.cs': {
    summary: 'Coordinates GC pressure during navigation transitions by tracking critical windows and deferring image surface releases until after the animation completes.',
    tags: ['service', 'memory-management', 'navigation'],
    complexity: 'moderate'
  },
  'src/Wavee.UI.WinUI/Services/NowPlayingHighlightService.cs': {
    summary: 'Tracks the currently playing track URI and notifies subscribers so track rows can highlight themselves when their track is playing.',
    tags: ['service', 'now-playing', 'event-handler'],
    complexity: 'moderate'
  },
  'src/Wavee.UI.WinUI/Services/NowPlayingPresentationService.cs': {
    summary: 'Manages the now-playing view presentation mode (normal, theatre, fullscreen) and exposes toggle and transition methods.',
    tags: ['service', 'now-playing', 'presentation'],
    complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Services/PageCache.cs': {
    summary: 'Generic async page cache with background refresh, suspend/resume control, and invalidation support used by paginated UI views.',
    tags: ['service', 'cache', 'pagination'],
    complexity: 'complex'
  },
  'src/Wavee.UI.WinUI/Services/PageHostCacheCleanupAdapter.cs': {
    summary: 'Adapts the page-host cache lifecycle to the cleanup scheduler, dropping stale entries for collapsed/destroyed page hosts on the UI thread.',
    tags: ['service', 'cache', 'lifecycle'],
    complexity: 'moderate'
  },
  'src/Wavee.UI.WinUI/Services/PaletteGradientCompositor.cs': {
    summary: 'Computes a gradient brush from a track palette (dominant colors) with system-theme-aware fallback for now-playing hero backgrounds.',
    tags: ['service', 'ui-composition', 'theming'],
    complexity: 'moderate'
  },
  'src/Wavee.UI.WinUI/Services/PathfinderSpotifyTrackSearcher.cs': {
    summary: 'Implements track search against the Spotify Pathfinder GraphQL API, returning structured track results for preview and link-resolution flows.',
    tags: ['service', 'search', 'spotify-api'],
    complexity: 'moderate'
  },
  'src/Wavee.UI.WinUI/Services/PhiSilicaStructuredTextGenerator.cs': {
    summary: 'Wraps the on-device Phi Silica model to generate structured (JSON) or plain text responses, with content-filter mapping and detailed diagnostic logging.',
    tags: ['service', 'ai', 'phi-silica'],
    complexity: 'complex',
    languageNotes: 'Uses Microsoft.Windows.AI.Text.LanguageModel (Phi Silica) for on-device inference; content filter options capped at Medium severity.'
  },
  'src/Wavee.UI.WinUI/Services/PhiSilicaStructuredTextPipeline.cs': {
    summary: 'Orchestrates multi-step Phi Silica inference with retry, fallback, policy sanity probes, token-length clamping, and diagnostic dump on failure.',
    tags: ['service', 'ai', 'phi-silica'],
    complexity: 'complex'
  },
  'src/Wavee.UI.WinUI/Services/PiiRedactor.cs': {
    summary: 'Scrubs PII patterns (email, bearer tokens, Windows/Unix home paths) from log strings before they are written to crash logs or debug output.',
    tags: ['utility', 'security', 'logging'],
    complexity: 'simple'
  },
  'src/Wavee.UI.WinUI/Services/PlaylistMosaicService.cs': {
    summary: 'Generates and disk-caches a 2x2 mosaic thumbnail for playlists by compositing cover art tiles from track images using Win2D canvas operations.',
    tags: ['service', 'image-processing', 'playlist'],
    complexity: 'complex'
  },
  'src/Wavee.UI.WinUI/Services/PlaylistPrefetcher.cs': {
    summary: 'Prefetches playlist metadata and warms the image cache for a batch of playlists, with priority queuing and partial-metadata construction from list v2 responses.',
    tags: ['service', 'prefetch', 'playlist'],
    complexity: 'complex'
  },
  'src/Wavee.UI.WinUI/Services/PlaylistPrefetchService.cs': {
    summary: 'Coordinates startup playlist prefetch across all library playlists, using PlaylistMetadataPrefetcher to batch-load metadata and warm caches.',
    tags: ['service', 'prefetch', 'playlist'],
    complexity: 'moderate'
  },
  'src/Wavee.UI.WinUI/Services/PreviewAudioGraphService.cs': {
    summary: 'Plays short audio preview clips via WinRT AudioGraph (primary) or MediaPlayer (fallback), with per-quantum FFT frame dispatch for visualizer consumers.',
    tags: ['service', 'audio', 'preview'],
    complexity: 'complex'
  },
  'src/Wavee.UI.WinUI/Services/ProfileCache.cs': {
    summary: 'Caches Spotify user profile data with diffing of followed artists and playlists to detect and surface incremental library changes.',
    tags: ['service', 'cache', 'profile'],
    complexity: 'moderate'
  },
  'src/Wavee.UI.WinUI/Services/ProfileFetcher.cs': {
    summary: 'Fetches and normalizes Spotify user profile metadata; ProfileService wraps it with an authenticated-user convenience loader.',
    tags: ['service', 'profile', 'spotify-api'],
    complexity: 'moderate'
  },
  'src/Wavee.UI.WinUI/Services/RecentlyPlayedService.cs': {
    summary: 'Maintains the recently-played shelf by merging Home-page recents with live playback context events, resolving artist/album/playlist metadata on demand.',
    tags: ['service', 'recently-played', 'home'],
    complexity: 'complex'
  },
  'src/Wavee.UI.WinUI/Services/RemoteStateRecorder.cs': {
    summary: 'Records dealer messages and HTTP calls (capped at 500 entries) for the Debug page, trimming oversized JSON payloads to keep memory bounded.',
    tags: ['service', 'debug', 'recording'],
    complexity: 'moderate'
  },
  'src/Wavee.UI.WinUI/Services/SettingsService.cs': {
    summary: 'Loads, persists, and debounced-saves app settings as JSON, with early-read peek helpers that bypass DI for log-level and cache-profile bootstrapping.',
    tags: ['service', 'settings', 'configuration'],
    complexity: 'complex'
  },
  'src/Wavee.UI.WinUI/Services/SharedCardCanvasPreviewService.cs': {
    summary: 'Manages a single shared MediaPlayerElement for card hover video previews, serializing acquire/release across callers on the UI thread.',
    tags: ['service', 'video-preview', 'ui-composition'],
    complexity: 'complex'
  },
  'src/Wavee.UI.WinUI/Services/ShellSessionService.cs': {
    summary: 'Persists and restores shell layout state (open tabs, sidebar selection, group expansions) across app sessions using a JSON-backed store.',
    tags: ['service', 'session', 'navigation'],
    complexity: 'complex'
  },
  'src/Wavee.UI.WinUI/Services/SpotifyLinkPreviewService.cs': {
    summary: 'Resolves Spotify entity URIs to preview cards (album, artist, track, show, episode, playlist, user) using Pathfinder GraphQL queries.',
    tags: ['service', 'spotify-api', 'preview'],
    complexity: 'moderate'
  }
};

// Build file nodes
data.results.forEach(r => {
  const meta = fileMeta[r.path] || {
    summary: 'WinUI service file.',
    tags: ['service'],
    complexity: 'moderate'
  };
  const fname = r.path.split('/').pop();
  const node = {
    id: 'file:' + r.path,
    type: 'file',
    name: fname,
    filePath: r.path,
    summary: meta.summary,
    tags: meta.tags,
    complexity: meta.complexity
  };
  if (meta.languageNotes) node.languageNotes = meta.languageNotes;
  nodes.push(node);
});

// Build class nodes for significant classes
data.results.forEach(r => {
  (r.classes || []).forEach(c => {
    if (!isSignificantClass(c)) return;
    const lines = c.endLine - c.startLine + 1;
    const complexity = lines > 200 ? 'complex' : lines > 50 ? 'moderate' : 'simple';
    const fname = r.path.split('/').pop().replace('.cs', '');
    nodes.push({
      id: 'class:' + r.path + ':' + c.name,
      type: 'class',
      name: c.name,
      filePath: r.path,
      lineRange: [c.startLine, c.endLine],
      summary: 'Class ' + c.name + ' in ' + fname + ' implementing ' + (c.methods ? c.methods.length : 0) + ' members.',
      tags: ['service', 'component'],
      complexity: complexity
    });
    edges.push({
      source: 'file:' + r.path,
      target: 'class:' + r.path + ':' + c.name,
      type: 'contains',
      direction: 'forward',
      weight: 1.0
    });
    edges.push({
      source: 'file:' + r.path,
      target: 'class:' + r.path + ':' + c.name,
      type: 'exports',
      direction: 'forward',
      weight: 0.8
    });
  });
});

// Build function nodes for significant functions (10+ lines)
// Track seen function IDs to avoid duplicates (overloads)
const seenFnIds = new Set();
data.results.forEach(r => {
  const fnCounts = {};
  (r.functions || []).forEach(f => {
    if (!isSignificantFunction(f)) return;
    // Handle overloads - append line number
    let id = 'function:' + r.path + ':' + f.name;
    if (seenFnIds.has(id)) {
      id = 'function:' + r.path + ':' + f.name + '_' + f.startLine;
    }
    seenFnIds.add(id);

    const lines = f.endLine - f.startLine + 1;
    const complexity = lines > 100 ? 'complex' : lines > 30 ? 'moderate' : 'simple';
    const fname = r.path.split('/').pop().replace('.cs', '');
    nodes.push({
      id: id,
      type: 'function',
      name: f.name,
      filePath: r.path,
      lineRange: [f.startLine, f.endLine],
      summary: 'Method ' + f.name + ' in ' + fname + ' (' + lines + ' lines).',
      tags: ['service'],
      complexity: complexity
    });
    edges.push({
      source: 'file:' + r.path,
      target: id,
      type: 'contains',
      direction: 'forward',
      weight: 1.0
    });
  });
});

console.log('Total nodes:', nodes.length);
console.log('Total edges:', edges.length);

const parts = Math.ceil(Math.max(nodes.length / 60, edges.length / 120));
console.log('Parts needed:', parts);

fs.writeFileSync('C:/WAVEE/WaveeMusic/.understand-anything/tmp/ua-batch46-graph.json', JSON.stringify({nodes, edges}));
console.log('Graph written');
