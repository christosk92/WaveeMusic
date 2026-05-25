const fs = require('fs');

const results35 = JSON.parse(fs.readFileSync('C:/WAVEE/WaveeMusic/.understand-anything/tmp/ua-file-extract-results-35.json', 'utf8'));

const nodes = [];
const edges = [];

function fileNode(path, summary, tags, complexity, languageNotes) {
  const n = {
    id: 'file:' + path,
    type: 'file',
    name: path.split('/').pop(),
    filePath: path,
    summary,
    tags,
    complexity
  };
  if (languageNotes) n.languageNotes = languageNotes;
  nodes.push(n);
}

function classNode(path, name, lineRange, summary, tags, complexity, languageNotes) {
  const n = {
    id: 'class:' + path + ':' + name,
    type: 'class',
    name,
    filePath: path,
    lineRange,
    summary,
    tags,
    complexity
  };
  if (languageNotes) n.languageNotes = languageNotes;
  nodes.push(n);
  edges.push({ source: 'file:' + path, target: 'class:' + path + ':' + name, type: 'contains', direction: 'forward', weight: 1.0 });
}

function funcNode(path, name, lineRange, summary, tags, complexity) {
  const n = {
    id: 'function:' + path + ':' + name,
    type: 'function',
    name,
    filePath: path,
    lineRange,
    summary,
    tags,
    complexity
  };
  nodes.push(n);
  edges.push({ source: 'file:' + path, target: 'function:' + path + ':' + name, type: 'contains', direction: 'forward', weight: 1.0 });
}

function getResult(path) {
  return results35.results.find(r => r.path === path);
}

function getFunc(path, name) {
  const r = getResult(path);
  if (!r) return null;
  return r.functions.find(fn => fn.name === name) || null;
}

function emitFuncs(path, names, descPrefix, tags) {
  names.forEach(name => {
    const fn = getFunc(path, name);
    if (fn) {
      funcNode(path, fn.name, [fn.startLine, fn.endLine],
        descPrefix + ' ' + fn.name + ' logic.',
        tags, 'moderate');
    }
  });
}

// ---- ISidebarItemModel.cs ----
const f1 = 'src/Wavee.UI.WinUI/Controls/Sidebar/ISidebarItemModel.cs';
fileNode(f1,
  'Defines the ISidebarItemModel interface contract for sidebar navigation items, including properties for display, drag-drop, pinning, and tooltip behavior.',
  ['type-definition', 'interface', 'sidebar', 'data-model'], 'moderate');
classNode(f1, 'ISidebarItemModel', [1, 57],
  'Interface contract for sidebar item data models, exposing navigation, drag-drop, pinning, icon, and context-menu properties.',
  ['type-definition', 'interface', 'sidebar', 'data-model'], 'moderate');

// ---- ISidebarViewModel.cs ----
const f2 = 'src/Wavee.UI.WinUI/Controls/Sidebar/ISidebarViewModel.cs';
fileNode(f2,
  'Declares the ISidebarViewModel interface for the sidebar view model, acting as a minimal binding contract between the sidebar control and its backing view model.',
  ['type-definition', 'interface', 'sidebar', 'component'], 'simple');

// ---- SidebarDisplayMode.cs ----
const f3 = 'src/Wavee.UI.WinUI/Controls/Sidebar/SidebarDisplayMode.cs';
fileNode(f3,
  'Defines the SidebarDisplayMode enum with Minimal, Compact, and Expanded values controlling how the sidebar renders at different widths.',
  ['type-definition', 'enum', 'sidebar', 'component'], 'simple');

// ---- SidebarItem.cs ----
const f4 = 'src/Wavee.UI.WinUI/Controls/Sidebar/SidebarItem.cs';
fileNode(f4,
  'Core WinUI control class for an individual sidebar navigation item, managing icon rendering, selection state, expand/collapse, drag-drop, pointer events, and display-mode transitions.',
  ['component', 'event-handler', 'sidebar', 'ui-control', 'drag-drop'], 'complex',
  'Partial class split with SidebarItem.Properties.cs; uses WinUI dependency properties, visual-state groups, and automation peer override pattern.');
classNode(f4, 'SidebarItem', [1, 1493],
  'WinUI ItemsControl-derived sidebar item handling template application, icon loading, selection, expand/collapse, and full drag-drop lifecycle including position-resolution for reorder and drop-in-folder.',
  ['component', 'event-handler', 'sidebar', 'ui-control', 'drag-drop'], 'complex');
emitFuncs(f4, [
  'OnApplyTemplate','Select','HandleItemChange','TryStartLazyIconLoad',
  'BuildSidebarDragPayload','SidebarItem_DragStarting','SidebarItem_DropCompleted',
  'ItemBorder_Drop','DetermineDropTargetPosition','UpdateSelectionState',
  'UpdateExpansionState','UpdateIcon','Clicked','RaiseItemInvoked',
  'ReevaluateSelection','ItemBorder_ContextRequested','OnGlobalDragStateChanged',
  'ChildItems_CollectionChanged'
], 'Handles', ['event-handler', 'component', 'sidebar']);

// ---- SidebarItem.Properties.cs ----
const f5 = 'src/Wavee.UI.WinUI/Controls/Sidebar/SidebarItem.Properties.cs';
fileNode(f5,
  'Partial class file declaring WinUI dependency properties and property-change callbacks for SidebarItem.',
  ['component', 'sidebar', 'data-model', 'type-definition'], 'moderate');

// ---- SidebarItemAutomationPeer.cs ----
const f6 = 'src/Wavee.UI.WinUI/Controls/Sidebar/SidebarItemAutomationPeer.cs';
fileNode(f6,
  'UIA automation peer for SidebarItem, implementing expand/collapse, invoke, and selection patterns to expose sidebar items to accessibility tools and UI testing frameworks.',
  ['component', 'sidebar', 'accessibility', 'automation'], 'moderate');
classNode(f6, 'SidebarItemAutomationPeer', [1, 115],
  'Automation peer implementing IExpandCollapseProvider, IInvokeProvider, and ISelectionItemProvider for sidebar items, enabling full UI automation support.',
  ['component', 'accessibility', 'automation', 'sidebar'], 'moderate');

// ---- SidebarItemDropPosition.cs ----
const f7 = 'src/Wavee.UI.WinUI/Controls/Sidebar/SidebarItemDropPosition.cs';
fileNode(f7,
  'Enum defining the possible drop positions (Before, In, After) when dragging items onto or around a sidebar row.',
  ['type-definition', 'enum', 'sidebar', 'drag-drop'], 'simple');

// ---- SidebarItemModel.cs ----
const f8 = 'src/Wavee.UI.WinUI/Controls/Sidebar/SidebarItemModel.cs';
fileNode(f8,
  'Observable data model backing a sidebar navigation item, implementing ISidebarItemModel with INotifyPropertyChanged for icon, label, badge, pin state, children, and drag-drop accept logic.',
  ['data-model', 'sidebar', 'component', 'event-handler'], 'complex');
classNode(f8, 'SidebarItemModel', [1, 314],
  'Concrete sidebar item model with observable properties for icon, text, badge count, pinned state, child items, and CanDrop logic used during drag-drop operations.',
  ['data-model', 'sidebar', 'component', 'event-handler'], 'complex');

// ---- SidebarStyles.xaml ----
const f9 = 'src/Wavee.UI.WinUI/Controls/Sidebar/SidebarStyles.xaml';
fileNode(f9,
  'XAML resource dictionary defining all visual styles, control templates, and visual state groups for SidebarItem and SidebarView, covering Minimal/Compact/Expanded display modes and drag-drop visual feedback.',
  ['component', 'sidebar', 'ui-control', 'configuration'], 'complex',
  'Large XAML resource dictionary; contains ControlTemplate with VisualStateGroups for pointer, selection, expansion, and display-mode states.');

// ---- SidebarView.Properties.cs ----
const f10 = 'src/Wavee.UI.WinUI/Controls/Sidebar/SidebarView.Properties.cs';
fileNode(f10,
  'Partial class declaring dependency properties and property-change handlers for SidebarView, including SelectedItem, DisplayMode, pane width, and item source bindings.',
  ['component', 'sidebar', 'data-model', 'type-definition'], 'moderate');

// ---- SidebarView.xaml ----
const f11 = 'src/Wavee.UI.WinUI/Controls/Sidebar/SidebarView.xaml';
fileNode(f11,
  'XAML template for SidebarView defining the panel structure: scroll area for navigation items, a resize grip, light-dismiss overlay, and pane open/close animation targets.',
  ['component', 'sidebar', 'ui-control', 'markup'], 'complex');

// ---- SidebarView.xaml.cs ----
const f12 = 'src/Wavee.UI.WinUI/Controls/Sidebar/SidebarView.xaml.cs';
fileNode(f12,
  'Code-behind for SidebarView implementing pane width animation, display-mode auto-switching based on available width, drag-state management, resizer interaction, and item virtualization callbacks.',
  ['component', 'sidebar', 'event-handler', 'animation'], 'complex');
classNode(f12, 'SidebarView', [1, 369],
  'WinUI ItemsControl hosting sidebar navigation items; manages pane width with animated offset, adaptive display-mode (Minimal/Compact/Expanded), resizer drag, and global drag-drop state coordination.',
  ['component', 'sidebar', 'event-handler', 'animation'], 'complex');
emitFuncs(f12, [
  'UpdateDisplayMode','ApplyPaneWidth','AnimateContentOffsetFrom',
  'UpdateDisplayModeForPaneWidth','SidebarResizer_ManipulationDelta',
  'MenuItemsHost_ElementPrepared'
], 'Handles', ['component', 'sidebar', 'event-handler']);

// ---- SidebarViewAutomationPeer.cs ----
const f13 = 'src/Wavee.UI.WinUI/Controls/Sidebar/SidebarViewAutomationPeer.cs';
fileNode(f13,
  'UIA automation peer for SidebarView, implementing ISelectionProvider to expose the current sidebar selection to accessibility tools.',
  ['component', 'sidebar', 'accessibility', 'automation'], 'simple');

// ---- ExpandedNowPlayingLayout.xaml ----
const f14 = 'src/Wavee.UI.WinUI/Controls/SidebarPlayer/ExpandedNowPlayingLayout.xaml';
fileNode(f14,
  'XAML layout for the expanded now-playing panel used inside the sidebar player, including audio/video chrome areas, album art, metadata, seek bar, and player action controls.',
  ['component', 'ui-control', 'player', 'markup'], 'complex');

// ---- ExpandedNowPlayingLayout.xaml.cs ----
const f15 = 'src/Wavee.UI.WinUI/Controls/SidebarPlayer/ExpandedNowPlayingLayout.xaml.cs';
fileNode(f15,
  'Code-behind for ExpandedNowPlayingLayout managing responsive audio/video sizing, video surface attachment/detachment, video takeover UX, heart/save state, seek callbacks, and composition-layer animations.',
  ['component', 'player', 'event-handler', 'animation', 'video'], 'complex');
classNode(f15, 'ExpandedNowPlayingLayout', [1, 1006],
  'Controls the expanded now-playing panel: orchestrates video surface ownership, theater mode, responsive layout scaling for audio art, animated metadata transitions, heart-state tracking, and video overlay auto-hide.',
  ['component', 'player', 'event-handler', 'animation', 'video'], 'complex');
emitFuncs(f15, [
  'OnLoaded','OnUnloaded','OnViewModelPropertyChanged','SetVideoSurfaceEnabled',
  'SetVideoPresentationMode','SetTheaterMode','OnActiveVideoSurfaceChanged',
  'UpdateVideoSurfaceOwnership','AttachSurface','DetachSurface',
  'ApplyResponsiveAudioSizing','UpdateHeartStateAsync',
  'UpdateVideoTakeoverSeenState','ApplyVideoOverlayMode','FadeVideoOverlay'
], 'Handles', ['component', 'player', 'event-handler']);

// ---- ExpandedPlayerContentMode.cs ----
const f16 = 'src/Wavee.UI.WinUI/Controls/SidebarPlayer/ExpandedPlayerContentMode.cs';
fileNode(f16,
  'Enum defining the content modes (Audio, Video, Lyrics, Queue) available in the expanded player view panel.',
  ['type-definition', 'enum', 'player', 'component'], 'simple');

// ---- ExpandedPlayerView.xaml ----
const f17 = 'src/Wavee.UI.WinUI/Controls/SidebarPlayer/ExpandedPlayerView.xaml';
fileNode(f17,
  'XAML template for the full expanded player view, hosting the now-playing layout alongside optional lyrics canvas and queue panel in a responsive column layout.',
  ['component', 'player', 'ui-control', 'markup'], 'complex');

// ---- ExpandedPlayerView.xaml.cs ----
const f18 = 'src/Wavee.UI.WinUI/Controls/SidebarPlayer/ExpandedPlayerView.xaml.cs';
fileNode(f18,
  'Code-behind for ExpandedPlayerView orchestrating mode switching (compact/focus/two-column), lyrics canvas lifecycle with color-palette tinting and timeline sync, queue panel integration, and responsive layout measurement passes.',
  ['component', 'player', 'event-handler', 'animation', 'lyrics'], 'complex',
  'Manages both the lyrics NowPlayingCanvas and the video surface concurrently; uses DispatcherTimer for lyrics sync and tint animation loops.');
classNode(f18, 'ExpandedPlayerView', [1, 1364],
  'Full expanded player host controlling adaptive layout width, lyrics canvas initialization/teardown with color tinting, queue panel toggle, video surface coordination, and compact-header mode transitions.',
  ['component', 'player', 'event-handler', 'animation', 'lyrics'], 'complex');
emitFuncs(f18, [
  'OnLoaded','OnUnloaded','ReleaseHeavyResources','OnViewModelPropertyChanged',
  'ApplyMode','SyncContentHostWidth','UpdateLyricsConsumerActivity',
  'EnsureLyricsCanvasRealized','InitializeLyricsCanvas','TeardownLyricsCanvas',
  'ApplyCurrentLyricsState','UpdateLyricsRenderState',
  'OnLyricsTimelineSyncTimerTick','ApplyCanvasSurfaceTint','AnimateAmbient'
], 'Handles', ['component', 'player', 'event-handler']);

// ---- LyricsAiPanel.xaml ----
const f19 = 'src/Wavee.UI.WinUI/Controls/SidebarPlayer/LyricsAiPanel.xaml';
fileNode(f19,
  'XAML layout for the on-device AI lyrics panel, showing AI-generated lyric summaries or explanations in a collapsible card within the expanded player.',
  ['component', 'player', 'lyrics', 'markup'], 'moderate');

// ---- LyricsAiPanel.xaml.cs ----
const f20 = 'src/Wavee.UI.WinUI/Controls/SidebarPlayer/LyricsAiPanel.xaml.cs';
fileNode(f20,
  'Code-behind for LyricsAiPanel managing animated card height transitions between compact and expanded states driven by the AI lyrics view model.',
  ['component', 'player', 'lyrics', 'animation'], 'moderate');
classNode(f20, 'LyricsAiPanel', [1, 259],
  'Animatable panel for the on-device AI lyrics feature; calculates compact/expanded card dimensions responsively and drives smooth height animations via CompositionAnimation.',
  ['component', 'player', 'lyrics', 'animation'], 'moderate');

// ---- SidebarPlayerWidget.xaml ----
const f21 = 'src/Wavee.UI.WinUI/Controls/SidebarPlayer/SidebarPlayerWidget.xaml';
fileNode(f21,
  'XAML template for the compact sidebar player widget showing album art, track metadata, seek bar, and transport controls in the sidebar gutter, with video surface support.',
  ['component', 'player', 'ui-control', 'markup'], 'complex');

// ---- SidebarPlayerWidget.xaml.cs ----
const f22 = 'src/Wavee.UI.WinUI/Controls/SidebarPlayer/SidebarPlayerWidget.xaml.cs';
fileNode(f22,
  'Code-behind for SidebarPlayerWidget managing video surface attachment, hover-reveal animations, album art tap navigation, heart/save state, end-of-context dismissal, and tint color application.',
  ['component', 'player', 'event-handler', 'animation', 'video'], 'complex');
classNode(f22, 'SidebarPlayerWidget', [1, 694],
  'Compact sidebar player widget: coordinates video surface ownership with the expanded player, applies hover-reveal composition animations, handles transport control events, and routes album-art taps to album/context navigation.',
  ['component', 'player', 'event-handler', 'animation', 'video'], 'complex');
emitFuncs(f22, [
  'OnWidgetLoaded','OnWidgetUnloaded','OnViewModelPropertyChanged',
  'ApplyCollapseState','SetFloatingVideoSurfaceEnabled',
  'UpdateVideoSurfaceOwnership','ReleaseVideoSurfaceOwnership',
  'AttachSurface','DetachSurface','ApplyHoverReveal',
  'EnsureHoverAnimationsAttached','UpdateHeartStateAsync',
  'ApplyTintColor','NavigateToAlbum'
], 'Handles', ['component', 'player', 'event-handler']);

// ---- BootSplash.xaml ----
const f23 = 'src/Wavee.UI.WinUI/Controls/Splash/BootSplash.xaml';
fileNode(f23,
  'XAML template for the app boot splash screen shown during startup, with branding and a delayed loading ring.',
  ['component', 'ui-control', 'markup', 'entry-point'], 'simple');

// ---- BootSplash.xaml.cs ----
const f24 = 'src/Wavee.UI.WinUI/Controls/Splash/BootSplash.xaml.cs';
fileNode(f24,
  'Code-behind for BootSplash showing a delayed progress ring and then fading out the splash screen once initialization completes.',
  ['component', 'ui-control', 'animation', 'entry-point'], 'simple');
classNode(f24, 'BootSplash', [1, 72],
  'Startup splash control that delays the loading ring by a short interval, then fades out smoothly when the app initialization finishes.',
  ['component', 'ui-control', 'animation', 'entry-point'], 'simple');

// ---- SpotifyConnectDialog.xaml ----
const f25 = 'src/Wavee.UI.WinUI/Controls/SpotifyConnectDialog.xaml';
fileNode(f25,
  'XAML template for the Spotify Connect device-picker dialog, listing available remote playback devices with selection and transfer controls.',
  ['component', 'ui-control', 'markup', 'connect'], 'moderate');

// No import edges (all batchImportData arrays are empty)
// No cross-file edges (neighborMap is empty)

const output = { nodes, edges };
console.log('nodeCount:', nodes.length, 'edgeCount:', edges.length);

const outPath = 'C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-35.json';
fs.writeFileSync(outPath, JSON.stringify(output, null, 2));
console.log('Written:', outPath);
