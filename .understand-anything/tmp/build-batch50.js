const fs = require('fs');
const r = JSON.parse(fs.readFileSync('C:/WAVEE/WaveeMusic/.understand-anything/tmp/ua-file-extract-results-50.json', 'utf8'));

const nodes = [];
const edges = [];

function sigFn(fn) {
  return (fn.endLine - fn.startLine + 1) >= 10;
}

function sigClass(cls) {
  return (cls.methods && cls.methods.length >= 2) || ((cls.endLine - cls.startLine + 1) >= 20);
}

function processFile(f, filePath, summary, tags, complexity, languageNotes) {
  const fileId = 'file:' + filePath;
  const node = {
    id: fileId,
    type: 'file',
    name: filePath.split('/').pop(),
    filePath: filePath,
    summary: summary,
    tags: tags,
    complexity: complexity
  };
  if (languageNotes) node.languageNotes = languageNotes;
  nodes.push(node);

  if (f.classes) {
    f.classes.forEach(cls => {
      if (!sigClass(cls)) return;
      const lines = cls.endLine - cls.startLine + 1;
      const clsId = 'class:' + filePath + ':' + cls.name;
      nodes.push({
        id: clsId,
        type: 'class',
        name: cls.name,
        filePath: filePath,
        lineRange: [cls.startLine, cls.endLine],
        summary: cls.name + ' is a view model class defined in ' + filePath.split('/').pop() + '.',
        tags: ['view-model', 'component', 'data-model'],
        complexity: lines > 200 ? 'complex' : lines > 50 ? 'moderate' : 'simple'
      });
      edges.push({ source: fileId, target: clsId, type: 'contains', direction: 'forward', weight: 1.0 });
      edges.push({ source: fileId, target: clsId, type: 'exports', direction: 'forward', weight: 0.8 });
    });
  }

  if (f.functions) {
    f.functions.forEach(fn => {
      if (!sigFn(fn)) return;
      const fnLines = fn.endLine - fn.startLine + 1;
      const fnId = 'function:' + filePath + ':' + fn.name + '_L' + fn.startLine;
      const parentCls = f.classes && f.classes.find(c => sigClass(c) && c.startLine <= fn.startLine && c.endLine >= fn.endLine);
      nodes.push({
        id: fnId,
        type: 'function',
        name: fn.name,
        filePath: filePath,
        lineRange: [fn.startLine, fn.endLine],
        summary: fn.name + ' method in ' + (parentCls ? parentCls.name : filePath.split('/').pop()) + '.',
        tags: ['service', 'component'],
        complexity: fnLines > 50 ? 'complex' : 'moderate'
      });
      const containerId = parentCls ? ('class:' + filePath + ':' + parentCls.name) : fileId;
      edges.push({ source: containerId, target: fnId, type: 'contains', direction: 'forward', weight: 1.0 });
    });
  }
}

const fileSpecs = [
  {
    path: 'src/Wavee.UI.WinUI/ViewModels/LazyItemVm.cs',
    summary: 'Defines lazy-loading item view models (LazyItemVm, LazyTrackItem, LazyReleaseItem) that populate track and release card data on demand, deferring heavy property reads until the item enters the viewport.',
    tags: ['data-model', 'component', 'lazy-loading', 'view-model'],
    complexity: 'complex'
  },
  {
    path: 'src/Wavee.UI.WinUI/ViewModels/Library/DualSourceLibraryViewModelBase.cs',
    summary: 'Abstract base view model for library views that support both Spotify and local sources, providing preference persistence, source-mode switching, and filter/sort delegation.',
    tags: ['data-model', 'view-model', 'service', 'utility'],
    complexity: 'complex'
  },
  {
    path: 'src/Wavee.UI.WinUI/ViewModels/Library/LibrarySource.cs',
    summary: 'Defines the LibrarySource enum distinguishing between Spotify and Local collection sources used by dual-source library view models.',
    tags: ['data-model', 'type-definition', 'configuration'],
    complexity: 'simple'
  },
  {
    path: 'src/Wavee.UI.WinUI/ViewModels/Library/LibraryViewModelBase.cs',
    summary: 'Abstract base class for all library view models providing sort, search, view-mode, and grid-scale preference persistence along with recents subtitle formatting and lifecycle service attach/detach hooks.',
    tags: ['data-model', 'view-model', 'service', 'utility'],
    complexity: 'complex'
  },
  {
    path: 'src/Wavee.UI.WinUI/ViewModels/LibraryPageViewModel.cs',
    summary: 'View model for the main Library page, managing the active tab child view model lifetime and disposing it on navigation away.',
    tags: ['view-model', 'component', 'service'],
    complexity: 'simple'
  },
  {
    path: 'src/Wavee.UI.WinUI/ViewModels/LikedSongsViewModel.cs',
    summary: 'Feature-rich view model for the Liked Songs library page supporting filter chips, sort, search, playback (play all/shuffle/selected/track), queue operations, multi-select, track removal, and real-time Spotify/local library change subscription.',
    tags: ['view-model', 'service', 'component', 'event-handler'],
    complexity: 'complex'
  },
  {
    path: 'src/Wavee.UI.WinUI/ViewModels/Local/LocalCollectionDetailViewModel.cs',
    summary: 'View model for a local media collection detail page, loading items from the local library for a given collection identifier.',
    tags: ['view-model', 'component', 'data-model'],
    complexity: 'simple'
  },
  {
    path: 'src/Wavee.UI.WinUI/ViewModels/Local/LocalItemDetailFlyoutViewModel.cs',
    summary: 'View model for the local media item detail flyout supporting metadata loading/saving, kind classification, and artwork override for files in the local library.',
    tags: ['view-model', 'component', 'data-model'],
    complexity: 'complex'
  },
  {
    path: 'src/Wavee.UI.WinUI/ViewModels/Local/LocalLandingViewModel.cs',
    summary: 'Landing page view model for the Local media section, aggregating recent music videos and top-level local library items while tracking TMDB enrichment CTA state and responding to library changes.',
    tags: ['view-model', 'component', 'service'],
    complexity: 'complex'
  },
  {
    path: 'src/Wavee.UI.WinUI/ViewModels/Local/LocalLikedSongsViewModel.cs',
    summary: 'View model for the Local Liked Songs list, loading locally-liked tracks from the local media library.',
    tags: ['view-model', 'component', 'data-model'],
    complexity: 'simple'
  },
  {
    path: 'src/Wavee.UI.WinUI/ViewModels/Local/LocalMovieDetailViewModel.cs',
    summary: 'View model for a local movie detail page, loading movie metadata including cast and season/episode information from the local library.',
    tags: ['view-model', 'component', 'data-model'],
    complexity: 'moderate'
  },
  {
    path: 'src/Wavee.UI.WinUI/ViewModels/Local/LocalMoviesViewModel.cs',
    summary: 'View model for the Local Movies list, loading movie items from the local library and generating subtitle text.',
    tags: ['view-model', 'component', 'data-model'],
    complexity: 'simple'
  },
  {
    path: 'src/Wavee.UI.WinUI/ViewModels/Local/LocalMusicVideosViewModel.cs',
    summary: 'View model for the Local Music Videos list, handling library change events, async reload, result reconciliation, and refreshing linked Spotify track URIs for enriched video items.',
    tags: ['view-model', 'service', 'component', 'event-handler'],
    complexity: 'complex'
  },
  {
    path: 'src/Wavee.UI.WinUI/ViewModels/Local/LocalMusicViewModel.cs',
    summary: 'View model for the Local Music list, loading tracks from the local library with an optional liked-only filter toggle.',
    tags: ['view-model', 'component', 'data-model'],
    complexity: 'simple'
  },
  {
    path: 'src/Wavee.UI.WinUI/ViewModels/Local/LocalOtherViewModel.cs',
    summary: 'View model for the Local Other (unclassified) media list, loading miscellaneous local library items.',
    tags: ['view-model', 'component', 'data-model'],
    complexity: 'simple'
  },
  {
    path: 'src/Wavee.UI.WinUI/ViewModels/Local/LocalPersonDetailViewModel.cs',
    summary: 'View model for a local person (actor/director) detail page, prefilling display data and loading full metadata from the local library.',
    tags: ['view-model', 'component', 'data-model'],
    complexity: 'moderate'
  },
  {
    path: 'src/Wavee.UI.WinUI/ViewModels/Local/LocalShowDetailViewModel.cs',
    summary: 'View model for a local TV show detail page, loading episodes by season, handling season selection changes, picking play targets, and supporting mark-all-watched.',
    tags: ['view-model', 'component', 'service'],
    complexity: 'moderate'
  },
  {
    path: 'src/Wavee.UI.WinUI/ViewModels/Local/LocalShowsViewModel.cs',
    summary: 'View model for the Local Shows list, loading TV show items from the local library and generating subtitle text.',
    tags: ['view-model', 'component', 'data-model'],
    complexity: 'simple'
  },
  {
    path: 'src/Wavee.UI.WinUI/ViewModels/Local/UpNextEpisodeOverlayViewModel.cs',
    summary: 'View model driving the Up Next episode overlay shown near the end of a local video episode, computing trigger timing, managing a countdown timer, and issuing playback advance on confirmation or auto-advance.',
    tags: ['view-model', 'service', 'event-handler', 'component'],
    complexity: 'complex'
  },
  {
    path: 'src/Wavee.UI.WinUI/ViewModels/LocalFilesViewModel.cs',
    summary: 'View model for the Local Files settings page, managing watched folder additions/removals, full library rescans, TMDB token verification/clearing, and enrichment-enabled toggling.',
    tags: ['view-model', 'service', 'component', 'configuration'],
    complexity: 'complex'
  },
  {
    path: 'src/Wavee.UI.WinUI/ViewModels/LocalLibraryViewModel.cs',
    summary: 'View model for the Local Library landing, grouping local tracks by album and supporting track playback from within the local collection.',
    tags: ['view-model', 'component', 'data-model'],
    complexity: 'moderate'
  },
  {
    path: 'src/Wavee.UI.WinUI/ViewModels/LyricsAiPanelViewModel.cs',
    summary: 'View model for the on-device AI lyrics panel, orchestrating Phi Silica summarize/explain generation flows with cancellation, result dismissal, and expand/collapse toggling.',
    tags: ['view-model', 'service', 'component', 'ai'],
    complexity: 'complex'
  },
  {
    path: 'src/Wavee.UI.WinUI/ViewModels/LyricsViewModel.cs',
    summary: 'View model for the lyrics panel tracking playback state and position, deferring and loading lyrics, applying per-line style, creating sidebar window status, and building an accent color palette from current playback artwork.',
    tags: ['view-model', 'service', 'component', 'event-handler'],
    complexity: 'complex'
  },
  {
    path: 'src/Wavee.UI.WinUI/ViewModels/MainWindowViewModel.cs',
    summary: 'Minimal view model for MainWindow, providing theme toggle and a reference to the AppSettings singleton.',
    tags: ['view-model', 'component', 'singleton'],
    complexity: 'simple'
  },
  {
    path: 'src/Wavee.UI.WinUI/ViewModels/MiniVideoPlayerViewModel.cs',
    summary: 'View model for the mini floating video player, controlling visibility state based on sidebar/floating player/user dismiss/video-active conditions and coordinating show/restore/expand/close transitions.',
    tags: ['view-model', 'service', 'component', 'event-handler'],
    complexity: 'complex'
  }
];

r.results.forEach(f => {
  const spec = fileSpecs.find(s => s.path === f.path);
  if (spec) {
    processFile(f, f.path, spec.summary, spec.tags, spec.complexity, spec.languageNotes);
  }
});

console.log('Total nodes:', nodes.length);
console.log('Total edges:', edges.length);

const needsSplit = nodes.length > 60 || edges.length > 120;
console.log('Needs split:', needsSplit);
if (needsSplit) {
  const parts = Math.ceil(Math.max(nodes.length / 60, edges.length / 120));
  console.log('Parts needed:', parts);
}

fs.writeFileSync('C:/WAVEE/WaveeMusic/.understand-anything/tmp/ua-batch50-full.json', JSON.stringify({nodes, edges}));
console.log('Saved full data');
