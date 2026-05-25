const fs = require('fs');
const data = JSON.parse(fs.readFileSync('C:/WAVEE/WaveeMusic/.understand-anything/tmp/ua-file-extract-results-51.json', 'utf8'));

const nodes = [];
const edges = [];

function complexity(nonEmptyLines) {
  if (nonEmptyLines < 50) return 'simple';
  if (nonEmptyLines <= 200) return 'moderate';
  return 'complex';
}

function funcComplexity(lines) {
  if (lines < 20) return 'simple';
  if (lines <= 60) return 'moderate';
  return 'complex';
}

const fileMeta = {
  'src/Wavee.UI.WinUI/ViewModels/PlayerBarViewModel.cs': {
    summary: 'Reactive ViewModel for the player bar UI, synchronizing playback state (position, track metadata, chapters, podcast resume prompts) from playback and Connect services to the UI layer.',
    tags: ['viewmodel', 'component', 'event-handler', 'data-model', 'service']
  },
  'src/Wavee.UI.WinUI/ViewModels/Playlist/PlaylistHeaderViewModel.cs': {
    summary: 'Manages the playlist header state including collaborator resolution, follower counts, cover art palette extraction, and chart-header rendering for the playlist page.',
    tags: ['viewmodel', 'component', 'data-model', 'service', 'event-handler']
  },
  'src/Wavee.UI.WinUI/ViewModels/Playlist/PlaylistMutationCoordinator.cs': {
    summary: 'Coordinates all write operations on a playlist: rename, cover change, delete, collaborative toggle, description update, and AI-powered recommendation injection.',
    tags: ['viewmodel', 'service', 'component', 'event-handler', 'factory']
  },
  'src/Wavee.UI.WinUI/ViewModels/Playlist/PlaylistTrackListViewModel.cs': {
    summary: 'ViewModel for the playlist track list with filtering, sorting, session control chips (Spotify Connect queue integration), video availability fetching, and empty-playlist genre recommendations.',
    tags: ['viewmodel', 'component', 'data-model', 'event-handler', 'service']
  },
  'src/Wavee.UI.WinUI/ViewModels/PlaylistViewModel.cs': {
    summary: 'Top-level ViewModel for the playlist page, orchestrating header, track list, and mutation sub-ViewModels with lifecycle management, detail loading, and mosaic hero art composition.',
    tags: ['viewmodel', 'component', 'service', 'event-handler', 'factory']
  },
  'src/Wavee.UI.WinUI/ViewModels/PodcastBrowseViewModel.cs': {
    summary: 'ViewModel for the podcast browse/discovery surface, supporting category drilling, breadcrumb navigation, charts, hero slides, and paginated section loading from Pathfinder.',
    tags: ['viewmodel', 'component', 'data-model', 'service', 'event-handler']
  },
  'src/Wavee.UI.WinUI/ViewModels/PodcastCommentViewModel.cs': {
    summary: 'ViewModels for podcast episode comments and their replies, handling reply threading, reaction toggling, reply composition, and pagination.',
    tags: ['viewmodel', 'component', 'data-model', 'event-handler', 'service']
  },
  'src/Wavee.UI.WinUI/ViewModels/ProfileViewModel.cs': {
    summary: 'ViewModel for the user profile page, loading top tracks, follower/following counts, page bleed brush, follow toggling, and background refresh on re-activation.',
    tags: ['viewmodel', 'component', 'data-model', 'service', 'event-handler']
  },
  'src/Wavee.UI.WinUI/ViewModels/SearchViewModel.cs': {
    summary: 'ViewModel for the search results page, merging Spotify API and local library results across filters with chip-page pagination and a result cache.',
    tags: ['viewmodel', 'component', 'data-model', 'service', 'event-handler']
  },
  'src/Wavee.UI.WinUI/ViewModels/SectionFeedViewModelBase.cs': {
    summary: 'Abstract base ViewModel for section-feed pages providing common ReloadAsync and Dispose contract.',
    tags: ['viewmodel', 'component', 'utility', 'data-model', 'service']
  },
  'src/Wavee.UI.WinUI/ViewModels/SettingsViewModel.cs': {
    summary: 'Comprehensive settings ViewModel covering theme/language, audio quality, equalizer bands, lyrics sources, cache management, normalization, update checks, and log viewing.',
    tags: ['viewmodel', 'component', 'service', 'data-model', 'event-handler']
  },
  'src/Wavee.UI.WinUI/ViewModels/Shell/LinkPreviewCoordinator.cs': {
    summary: 'Coordinates link-paste preview in the omnibar by resolving a Spotify URI/URL to a typed preview entity with cancellation support.',
    tags: ['viewmodel', 'service', 'component', 'utility', 'event-handler']
  },
  'src/Wavee.UI.WinUI/ViewModels/Shell/OmnibarViewModel.cs': {
    summary: 'ViewModel for the shell omnibar, handling debounced suggestion fetching, recent searches, link paste previews, and navigation dispatch.',
    tags: ['viewmodel', 'component', 'service', 'event-handler', 'utility']
  },
  'src/Wavee.UI.WinUI/ViewModels/Shell/SidebarViewModel.cs': {
    summary: 'ViewModel for the navigation sidebar, loading library data (playlists, albums, artists, pins), managing pin/unpin actions, syncing selection, and persisting sidebar state.',
    tags: ['viewmodel', 'component', 'service', 'event-handler', 'data-model']
  },
  'src/Wavee.UI.WinUI/ViewModels/ShellViewModel.cs': {
    summary: 'Root shell ViewModel orchestrating tab management, player surface visibility, sidebar/right-panel docking, notification banners, and player location across the main window.',
    tags: ['viewmodel', 'component', 'service', 'event-handler', 'singleton']
  },
  'src/Wavee.UI.WinUI/ViewModels/ShowViewModel.cs': {
    summary: 'ViewModel for the podcast show page, loading show details and episodes with retry, applying theme, filtering/sorting episodes, and scheduling episode-progress refresh.',
    tags: ['viewmodel', 'component', 'service', 'data-model', 'event-handler']
  },
  'src/Wavee.UI.WinUI/ViewModels/SpotifyConnectViewModel.cs': {
    summary: 'ViewModel for Spotify Connect onboarding/login, supporting device-code OAuth, QR code generation, auth status callbacks, and library sync progress reporting.',
    tags: ['viewmodel', 'component', 'service', 'event-handler', 'api-handler']
  },
  'src/Wavee.UI.WinUI/ViewModels/StartPageViewModel.cs': {
    summary: 'Lightweight ViewModel for the app start/login page, populating quick-access items and delegating search navigation.',
    tags: ['viewmodel', 'component', 'data-model', 'entry-point', 'utility']
  },
  'src/Wavee.UI.WinUI/ViewModels/TrackDetails/ContributorVm.cs': {
    summary: 'Simple record ViewModel representing a single contributor (artist) entry in the track credits panel.',
    tags: ['data-model', 'viewmodel', 'component', 'utility', 'type-definition']
  },
  'src/Wavee.UI.WinUI/ViewModels/TrackDetails/CreditGroupVm.cs': {
    summary: 'Simple record ViewModel grouping contributors under a credit role label in the track details panel.',
    tags: ['data-model', 'viewmodel', 'component', 'utility', 'type-definition']
  },
  'src/Wavee.UI.WinUI/ViewModels/TrackDetails/EpisodeChapterVm.cs': {
    summary: 'ViewModel for a single podcast episode chapter, formatting its timestamp and exposing timeline highlight state.',
    tags: ['data-model', 'viewmodel', 'component', 'utility', 'event-handler']
  },
  'src/Wavee.UI.WinUI/ViewModels/TrackDetails/ExternalLinkVm.cs': {
    summary: 'Minimal record ViewModel carrying an external link (URL + label) in the track details panel.',
    tags: ['data-model', 'viewmodel', 'type-definition', 'utility', 'component']
  },
  'src/Wavee.UI.WinUI/ViewModels/TrackDetails/RelatedVideoVm.cs': {
    summary: 'Minimal record ViewModel for a related video entry (thumbnail, title, URI) in the track details panel.',
    tags: ['data-model', 'viewmodel', 'type-definition', 'utility', 'component']
  },
  'src/Wavee.UI.WinUI/ViewModels/TrackDetailsViewModel.cs': {
    summary: 'ViewModel for the track/episode detail right-panel, loading credits, related videos, canvas art, lyrics metadata, and podcast chapters; handles deferred loading and canvas override actions.',
    tags: ['viewmodel', 'component', 'service', 'data-model', 'event-handler']
  },
  'src/Wavee.UI.WinUI/ViewModels/YourEpisodesViewModel.cs': {
    summary: 'ViewModel for the Your Episodes library view, grouping saved podcast episodes by show with expandable groups, scope filtering, column width persistence, and episode progress tracking.',
    tags: ['viewmodel', 'component', 'service', 'data-model', 'event-handler']
  }
};

data.results.forEach(function(r) {
  const meta = fileMeta[r.path] || { summary: 'ViewModel file.', tags: ['viewmodel', 'component', 'service'] };
  const fileId = 'file:' + r.path;

  nodes.push({
    id: fileId,
    type: 'file',
    name: r.path.split('/').pop(),
    filePath: r.path,
    summary: meta.summary,
    tags: meta.tags,
    complexity: complexity(r.nonEmptyLines)
  });

  if (r.classes) {
    r.classes.forEach(function(c) {
      const lines = c.endLine - c.startLine;
      const methods = c.methods ? c.methods.length : 0;
      if (methods >= 2 || lines >= 20) {
        const classId = 'class:' + r.path + ':' + c.name;
        const cxp = lines >= 200 ? 'complex' : lines >= 50 ? 'moderate' : 'simple';
        nodes.push({
          id: classId,
          type: 'class',
          name: c.name,
          filePath: r.path,
          lineRange: [c.startLine, c.endLine],
          summary: 'ViewModel class ' + c.name + ' providing UI state and commands for ' + r.path.split('/').pop().replace('.cs', '') + '.',
          tags: ['viewmodel', 'component', 'service', 'data-model'],
          complexity: cxp
        });
        edges.push({ source: fileId, target: classId, type: 'contains', direction: 'forward', weight: 1.0 });
      }
    });
  }

  if (r.functions) {
    r.functions.forEach(function(f) {
      const lines = f.endLine - f.startLine;
      if (lines >= 10) {
        const fnId = 'function:' + r.path + ':' + f.name;
        nodes.push({
          id: fnId,
          type: 'function',
          name: f.name,
          filePath: r.path,
          lineRange: [f.startLine, f.endLine],
          summary: 'Method ' + f.name + ' in ' + r.path.split('/').pop().replace('.cs', '') + '.',
          tags: ['service', 'event-handler'],
          complexity: funcComplexity(lines)
        });
        edges.push({ source: fileId, target: fnId, type: 'contains', direction: 'forward', weight: 1.0 });
      }
    });
  }
});

console.log('Total nodes:', nodes.length);
console.log('Total edges:', edges.length);

fs.writeFileSync('C:/WAVEE/WaveeMusic/.understand-anything/tmp/ua-graph-51.json', JSON.stringify({ nodes: nodes, edges: edges }));
console.log('Graph written');
