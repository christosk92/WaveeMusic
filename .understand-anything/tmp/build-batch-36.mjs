import { readFileSync, writeFileSync } from 'fs';

const data = JSON.parse(readFileSync('C:/WAVEE/WaveeMusic/.understand-anything/tmp/ua-file-extract-results-36.json', 'utf8'));

function isSignificantFunc(f) { return (f.endLine - f.startLine) >= 10; }
function isSignificantClass(c) { return c.methods.length >= 2 || (c.endLine - c.startLine) >= 20; }
function fileComplexity(r) {
  if (r.nonEmptyLines > 200) return 'complex';
  if (r.nonEmptyLines >= 50) return 'moderate';
  return 'simple';
}
function classComplexity(c) {
  const lines = c.endLine - c.startLine;
  if (lines > 200) return 'complex';
  if (lines > 50) return 'moderate';
  return 'simple';
}
function funcComplexity(f) {
  const lines = f.endLine - f.startLine;
  if (lines > 50) return 'complex';
  if (lines > 20) return 'moderate';
  return 'simple';
}

const summaries = {
  'src/Wavee.UI.WinUI/Controls/SpotifyConnectDialog.xaml.cs': 'Code-behind for the Spotify Connect dialog, managing QR code visibility, connect code clipboard copy, and ViewModel property change reactions.',
  'src/Wavee.UI.WinUI/Controls/StackedAvatars.cs': 'Custom WinUI control rendering overlapping circular avatars for a list of collaborators, with configurable overlap, ring thickness, and overflow count badge.',
  'src/Wavee.UI.WinUI/Controls/TabBar/INavCacheSurfaceParticipant.cs': 'Interface for controls that release and restore GPU composition surfaces when their parent tab is placed into the navigation cache.',
  'src/Wavee.UI.WinUI/Controls/TabBar/INavigationCacheMemoryParticipant.cs': 'Interface for pages that perform multi-step memory trimming and restoration when their tab is deactivated in the navigation cache.',
  'src/Wavee.UI.WinUI/Controls/TabBar/ITabBar.cs': 'Contract for the TabBar host control exposing tab collection, selection state, and close/reopen operations.',
  'src/Wavee.UI.WinUI/Controls/TabBar/ITabBarItem.cs': 'Interface defining the public surface of a single tab bar item: header, icon, tooltip, content host, and navigation parameter.',
  'src/Wavee.UI.WinUI/Controls/TabBar/ITabBarItemContent.cs': 'Interface for page content hosted in a tab, enabling parameter-based navigation refresh and same-parameter reuse optimisation.',
  'src/Wavee.UI.WinUI/Controls/TabBar/ITabSleepParticipant.cs': 'Interface for pages that capture and restore visual state across the tab Sleep and Wake lifecycle.',
  'src/Wavee.UI.WinUI/Controls/TabBar/NavCacheSurfaces.cs': 'Static utility walking the visual tree to orchestrate INavCacheSurfaceParticipant GPU surface release and restore, and to sum estimated off-screen composition memory.',
  'src/Wavee.UI.WinUI/Controls/TabBar/TabBar.xaml': 'XAML template for the TabBar control defining the WinUI TabView layout with drag region, add-tab button, and per-item context menu.',
  'src/Wavee.UI.WinUI/Controls/TabBar/TabBar.xaml.cs': 'Code-behind for the TabBar control implementing selection, close, pin, compact, sleep, and sort operations over the WinUI TabView.',
  'src/Wavee.UI.WinUI/Controls/TabBar/TabBarItem.cs': 'Core tab navigation host managing per-tab page navigation, adaptive frame-cache sizing, deferred memory trimming, GPU surface retention, and tab sleep/wake lifecycle.',
  'src/Wavee.UI.WinUI/Controls/Track/Behaviors/TrackBehavior.cs': 'Attached-property behavior attaching tap, double-tap, right-tap, pointer-hover, and context-menu handling to FrameworkElements hosting track rows.',
  'src/Wavee.UI.WinUI/Controls/Track/Behaviors/TrackStateBehavior.cs': 'Attached-property behavior maintaining a global weak-reference registry mapping Spotify track IDs to track row elements, updating IsPlaying/IsPaused visual states on playback change.',
  'src/Wavee.UI.WinUI/Controls/Track/TrackItem.AddToPlaylist.cs': 'Partial TrackItem providing add-to-playlist affordance wiring: hooks IAddToPlaylistSession and toggles the track in the current pending add session.',
  'src/Wavee.UI.WinUI/Controls/Track/TrackItem.Click.cs': 'Partial TrackItem handling click, tap, double-tap, heart/save, artist/album link navigation, and play-command invocation including video-track save-target resolution.',
  'src/Wavee.UI.WinUI/Controls/Track/TrackItem.Hover.cs': 'Partial TrackItem managing pointer-enter/exit hover state, driving play-button and action-control visibility in row and compact modes.',
  'src/Wavee.UI.WinUI/Controls/Track/TrackItem.ModeAndLoading.cs': 'Partial TrackItem handling mode switching between row, compact, and media-item display, column width propagation, loading state, and deferred album-art realization.',
  'src/Wavee.UI.WinUI/Controls/Track/TrackItem.Playback.cs': 'Partial TrackItem managing playback state subscription, now-playing indicator, buffering indicator, and per-track visual state driven by IPlaybackStateService.',
  'src/Wavee.UI.WinUI/Controls/Track/TrackItem.Selection.cs': 'Partial TrackItem handling multi-select mode toggling and per-row checkbox selection state.',
  'src/Wavee.UI.WinUI/Controls/Track/TrackItem.xaml': 'XAML definition for the TrackItem control containing row, compact, and media-item visual templates with album art, metadata columns, progress bar, and action buttons.',
  'src/Wavee.UI.WinUI/Controls/Track/TrackItem.xaml.cs': 'Main partial TrackItem code-behind: dependency properties, constructor, track data binding, album art realization, chart status, liked-state refresh, observable track subscriptions, and context menu entry point.',
  'src/Wavee.UI.WinUI/Controls/TrackDataGrid/AddedByCellInfo.cs': 'Simple data record holding display information for the Added-By column cell in the TrackDataGrid.',
  'src/Wavee.UI.WinUI/Controls/TrackDataGrid/LoadingRowConfig.cs': 'Configuration record for TrackDataGrid loading skeleton rows, holding column visibility and width hints for placeholder shimmer rendering.',
  'src/Wavee.UI.WinUI/Controls/TrackDataGrid/TrackDataGrid.Columns.cs': 'Partial TrackDataGrid managing column configuration: header rebuilding, sort flyout, column-width persistence, per-row flag propagation, and visibility rules for optional columns.'
};

const fileTags = {
  'src/Wavee.UI.WinUI/Controls/SpotifyConnectDialog.xaml.cs': ['component', 'event-handler', 'spotify-connect', 'dialog'],
  'src/Wavee.UI.WinUI/Controls/StackedAvatars.cs': ['component', 'custom-control', 'avatars', 'ui-layout'],
  'src/Wavee.UI.WinUI/Controls/TabBar/INavCacheSurfaceParticipant.cs': ['type-definition', 'navigation-cache', 'memory-management', 'interface'],
  'src/Wavee.UI.WinUI/Controls/TabBar/INavigationCacheMemoryParticipant.cs': ['type-definition', 'navigation-cache', 'memory-management', 'interface'],
  'src/Wavee.UI.WinUI/Controls/TabBar/ITabBar.cs': ['type-definition', 'tab-bar', 'navigation', 'interface'],
  'src/Wavee.UI.WinUI/Controls/TabBar/ITabBarItem.cs': ['type-definition', 'tab-bar', 'navigation', 'interface'],
  'src/Wavee.UI.WinUI/Controls/TabBar/ITabBarItemContent.cs': ['type-definition', 'tab-bar', 'navigation', 'interface'],
  'src/Wavee.UI.WinUI/Controls/TabBar/ITabSleepParticipant.cs': ['type-definition', 'tab-bar', 'lifecycle', 'interface'],
  'src/Wavee.UI.WinUI/Controls/TabBar/NavCacheSurfaces.cs': ['utility', 'navigation-cache', 'gpu-surface', 'memory-management'],
  'src/Wavee.UI.WinUI/Controls/TabBar/TabBar.xaml': ['markup', 'tab-bar', 'component', 'xaml-template'],
  'src/Wavee.UI.WinUI/Controls/TabBar/TabBar.xaml.cs': ['component', 'tab-bar', 'event-handler', 'navigation'],
  'src/Wavee.UI.WinUI/Controls/TabBar/TabBarItem.cs': ['component', 'tab-bar', 'navigation', 'memory-management'],
  'src/Wavee.UI.WinUI/Controls/Track/Behaviors/TrackBehavior.cs': ['behavior', 'attached-property', 'event-handler', 'track-ui'],
  'src/Wavee.UI.WinUI/Controls/Track/Behaviors/TrackStateBehavior.cs': ['behavior', 'attached-property', 'playback-state', 'track-ui'],
  'src/Wavee.UI.WinUI/Controls/Track/TrackItem.AddToPlaylist.cs': ['component', 'track-ui', 'playlist', 'event-handler'],
  'src/Wavee.UI.WinUI/Controls/Track/TrackItem.Click.cs': ['component', 'track-ui', 'event-handler', 'playback'],
  'src/Wavee.UI.WinUI/Controls/Track/TrackItem.Hover.cs': ['component', 'track-ui', 'hover-state', 'event-handler'],
  'src/Wavee.UI.WinUI/Controls/Track/TrackItem.ModeAndLoading.cs': ['component', 'track-ui', 'mode-switching', 'loading-state'],
  'src/Wavee.UI.WinUI/Controls/Track/TrackItem.Playback.cs': ['component', 'track-ui', 'playback-state', 'subscription'],
  'src/Wavee.UI.WinUI/Controls/Track/TrackItem.Selection.cs': ['component', 'track-ui', 'selection', 'multi-select'],
  'src/Wavee.UI.WinUI/Controls/Track/TrackItem.xaml': ['markup', 'track-ui', 'component', 'xaml-template'],
  'src/Wavee.UI.WinUI/Controls/Track/TrackItem.xaml.cs': ['component', 'track-ui', 'data-binding', 'entry-point'],
  'src/Wavee.UI.WinUI/Controls/TrackDataGrid/AddedByCellInfo.cs': ['data-model', 'track-datagrid', 'type-definition'],
  'src/Wavee.UI.WinUI/Controls/TrackDataGrid/LoadingRowConfig.cs': ['data-model', 'track-datagrid', 'loading-state'],
  'src/Wavee.UI.WinUI/Controls/TrackDataGrid/TrackDataGrid.Columns.cs': ['component', 'track-datagrid', 'column-management', 'sort']
};

// Sort files alphabetically
const sortedResults = [...data.results].sort((a, b) => a.path.localeCompare(b.path));

const allNodes = [];
const allEdges = [];

sortedResults.forEach(r => {
  const fnode = {
    id: 'file:' + r.path,
    type: 'file',
    name: r.path.split('/').pop(),
    filePath: r.path,
    summary: summaries[r.path] || 'WinUI control file.',
    tags: fileTags[r.path] || ['component', 'winui'],
    complexity: fileComplexity(r)
  };
  allNodes.push(fnode);

  (r.classes||[]).forEach(c => {
    if (!isSignificantClass(c)) return;
    const cid = 'class:' + r.path + ':' + c.name;
    const methodsDesc = c.methods.length > 0
      ? 'Contains methods: ' + c.methods.slice(0, 4).join(', ') + (c.methods.length > 4 ? ' and ' + (c.methods.length - 4) + ' more.' : '.')
      : '';
    allNodes.push({
      id: cid, type: 'class', name: c.name, filePath: r.path,
      lineRange: [c.startLine, c.endLine],
      summary: 'Class ' + c.name + ' (' + r.path.split('/').pop() + '). ' + methodsDesc,
      tags: (fileTags[r.path] || ['component']).slice(0, 4),
      complexity: classComplexity(c)
    });
    allEdges.push({ source: 'file:' + r.path, target: cid, type: 'contains', direction: 'forward', weight: 1.0 });
    if ((r.exports||[]).some(e => e.name === c.name)) {
      allEdges.push({ source: 'file:' + r.path, target: cid, type: 'exports', direction: 'forward', weight: 0.8 });
    }
  });

  (r.functions||[]).forEach(f => {
    if (!isSignificantFunc(f)) return;
    const fid = 'function:' + r.path + ':' + f.name;
    const parentClass = (r.classes||[]).find(c => isSignificantClass(c) && c.startLine <= f.startLine && c.endLine >= f.endLine);
    const parentId = parentClass ? 'class:' + r.path + ':' + parentClass.name : 'file:' + r.path;
    allNodes.push({
      id: fid, type: 'function', name: f.name, filePath: r.path,
      lineRange: [f.startLine, f.endLine],
      summary: 'Function ' + f.name + ' in ' + r.path.split('/').pop() + ' (lines ' + f.startLine + '-' + f.endLine + ').',
      tags: (fileTags[r.path] || ['component']).slice(0, 3),
      complexity: funcComplexity(f)
    });
    allEdges.push({ source: parentId, target: fid, type: 'contains', direction: 'forward', weight: 1.0 });
    if ((r.exports||[]).some(e => e.name === f.name)) {
      allEdges.push({ source: 'file:' + r.path, target: fid, type: 'exports', direction: 'forward', weight: 0.8 });
    }
  });
});

console.log('Total nodes:', allNodes.length);
console.log('Total edges:', allEdges.length);

// Partition into 3 parts by file (sorted alphabetically)
const PARTS = 3;
const chunkSize = Math.ceil(sortedResults.length / PARTS);

for (let p = 0; p < PARTS; p++) {
  const partFiles = sortedResults.slice(p * chunkSize, (p + 1) * chunkSize).map(r => r.path);
  const partFilePaths = new Set(partFiles);
  const partNodes = allNodes.filter(n => partFilePaths.has(n.filePath));
  const partNodeIds = new Set(partNodes.map(n => n.id));
  const partEdges = allEdges.filter(e => partNodeIds.has(e.source));

  const outPath = 'C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-36-part-' + (p + 1) + '.json';
  writeFileSync(outPath, JSON.stringify({ nodes: partNodes, edges: partEdges }, null, 2), { encoding: 'utf8' });
  console.log('Part ' + (p + 1) + ': nodes=' + partNodes.length + ' edges=' + partEdges.length + ' files=' + partFiles.length);
}
