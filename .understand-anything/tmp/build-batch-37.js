const fs = require('fs');

const nodes = [];
const edges = [];

function fileNode(path, summary, tags, complexity, languageNotes) {
  const name = path.split('/').pop();
  const n = { id: 'file:' + path, type: 'file', name, filePath: path, summary, tags, complexity };
  if (languageNotes) n.languageNotes = languageNotes;
  nodes.push(n);
}
function classNode(path, className, lineRange, summary, tags, complexity) {
  nodes.push({ id: 'class:' + path + ':' + className, type: 'class', name: className, filePath: path, lineRange, summary, tags, complexity });
  edges.push({ source: 'file:' + path, target: 'class:' + path + ':' + className, type: 'contains', direction: 'forward', weight: 1.0 });
  edges.push({ source: 'file:' + path, target: 'class:' + path + ':' + className, type: 'exports', direction: 'forward', weight: 0.8 });
}
function funcNode(path, parentClass, funcName, lineRange, summary, tags, complexity) {
  nodes.push({ id: 'function:' + path + ':' + funcName, type: 'function', name: funcName, filePath: path, lineRange, summary, tags, complexity });
  edges.push({ source: 'file:' + path, target: 'function:' + path + ':' + funcName, type: 'contains', direction: 'forward', weight: 1.0 });
  if (parentClass) {
    edges.push({ source: 'class:' + path + ':' + parentClass, target: 'function:' + path + ':' + funcName, type: 'contains', direction: 'forward', weight: 1.0 });
  }
}

// ---- TrackDataGrid.RowContextMenu.cs ----
const P1 = 'src/Wavee.UI.WinUI/Controls/TrackDataGrid/TrackDataGrid.RowContextMenu.cs';
fileNode(P1,
  'Partial TrackDataGrid class handling right-tap and hold context menus for track rows, managing multi-select context menu commands (play next, add to queue, like, remove).',
  ['component', 'event-handler', 'context-menu', 'track-grid'], 'complex');
classNode(P1, 'TrackDataGrid', [48, 435],
  'Partial class fragment wiring right-tap/hold events, resolving clicked rows, capturing selection, building context menu items, and dispatching default play-next/queue/like/remove actions.',
  ['component', 'event-handler', 'context-menu'], 'complex');
funcNode(P1, 'TrackDataGrid', 'BuildSelectionMenuItems', [296, 354],
  'Builds the context menu item list for a multi-track selection, conditionally including play-next, add-to-queue, like-toggle, and remove actions based on command availability.',
  ['factory', 'context-menu'], 'moderate');

// ---- TrackDataGrid.SelectionMode.cs ----
const P2 = 'src/Wavee.UI.WinUI/Controls/TrackDataGrid/TrackDataGrid.SelectionMode.cs';
fileNode(P2,
  'Partial TrackDataGrid class managing selection mode lifecycle (enter/exit, Ctrl+A select-all, per-row toggle) and routing play/queue/like/remove actions on the current selection.',
  ['component', 'event-handler', 'selection'], 'complex');
classNode(P2, 'TrackDataGrid', [25, 208],
  'Partial class fragment controlling selection mode: toggling enter/exit, syncing the selection toggle button, handling keyboard shortcuts, and delegating selection commands.',
  ['component', 'selection', 'event-handler'], 'complex');

// ---- TrackDataGrid.xaml ----
const P3 = 'src/Wavee.UI.WinUI/Controls/TrackDataGrid/TrackDataGrid.xaml';
fileNode(P3,
  'XAML control template for TrackDataGrid defining the visual tree: rows ItemsView, skeleton loading rows, column header host, filter bar, density slider, toolbar, and drop indicator.',
  ['component', 'markup', 'track-grid'], 'complex');

// ---- TrackDataGrid.xaml.cs ----
const P4 = 'src/Wavee.UI.WinUI/Controls/TrackDataGrid/TrackDataGrid.xaml.cs';
fileNode(P4,
  'Core partial class for TrackDataGrid managing row virtualization, lazy-item subscriptions, column/row layout, grouping, text filtering, sort projection, density, drag-drop reorder, keyboard navigation, and disposal.',
  ['component', 'data-grid', 'track-grid', 'virtualization'], 'complex',
  'Large partial class (~1600 lines) split with RowContextMenu and SelectionMode partials; uses ItemsView for GPU-accelerated row virtualization.');
classNode(P4, 'TrackDataGrid', [42, 1624],
  'Primary TrackDataGrid WinUI 3 control: virtualizes rows, manages visible/lazy row sets, projects items through filter/sort/grouping into a flat observable list, and coordinates drag-drop reorder with drop indicator.',
  ['component', 'data-grid', 'virtualization'], 'complex');
funcNode(P4, 'TrackDataGrid', 'ReprojectRows', [1076, 1110],
  'Re-projects items through optional text filter, column sort, and optional grouping into the visible rows observable, restoring selection by track key after each projection.',
  ['data-model', 'filtering', 'sorting'], 'moderate');
funcNode(P4, 'TrackDataGrid', 'BuildFlatRowsWithHeaders', [1143, 1190],
  'Flattens grouped ITrackItem collections into a mixed object list with injected TrackDataGridGroupRow sentinels for rendering in the flat ItemsView.',
  ['data-model', 'grouping'], 'moderate');
funcNode(P4, 'TrackDataGrid', 'BuildLoadingRowConfigTemplate', [574, 654],
  'Computes a LoadingRowConfig that mirrors current column pixel widths for shimmer skeleton rows shown during content loading.',
  ['factory', 'shimmer', 'data-grid'], 'complex');
funcNode(P4, 'TrackDataGrid', 'Dispose', [1402, 1476],
  'Tears down all event handlers, clears row tracking collections, disposes lazy-item subscriptions, and resets drag state on control teardown.',
  ['lifecycle', 'cleanup'], 'moderate');

// ---- TrackDataGridColumn.cs ----
const P5 = 'src/Wavee.UI.WinUI/Controls/TrackDataGrid/TrackDataGridColumn.cs';
fileNode(P5,
  'Observable data model for a TrackDataGrid column descriptor storing key, header resource, cell template, sort key, visibility, and width constraints with INotifyPropertyChanged.',
  ['data-model', 'component', 'track-grid'], 'moderate');
classNode(P5, 'TrackDataGridColumn', [15, 111],
  'Column definition for TrackDataGrid with observable properties (key, cell template, sort key, length, visibility) notifying bindings via INotifyPropertyChanged.',
  ['data-model', 'component'], 'moderate');

// ---- TrackDataGridColumnHeader.xaml ----
const P6 = 'src/Wavee.UI.WinUI/Controls/TrackDataGrid/TrackDataGridColumnHeader.xaml';
fileNode(P6,
  'XAML template for TrackDataGridColumnHeader displaying a sortable column header with label text, sort direction arrow indicator, and visual state transitions for sort states.',
  ['component', 'markup', 'track-grid'], 'moderate');

// ---- TrackDataGridColumnHeader.xaml.cs ----
const P7 = 'src/Wavee.UI.WinUI/Controls/TrackDataGrid/TrackDataGridColumnHeader.xaml.cs';
fileNode(P7,
  'Code-behind for TrackDataGridColumnHeader exposing Header, CanBeSorted, ColumnSortOption, Command, and LabelPadding dependency properties with visual state transitions on sort direction change.',
  ['component', 'event-handler', 'track-grid'], 'moderate');
classNode(P7, 'TrackDataGridColumnHeader', [12, 96],
  'WinUI 3 sortable column header control whose ColumnSortOption DP drives Ascending/Descending/Unsorted visual states via VisualStateManager.',
  ['component', 'event-handler'], 'moderate');

// ---- TrackDataGridColumns.cs ----
const P8 = 'src/Wavee.UI.WinUI/Controls/TrackDataGrid/TrackDataGridColumns.cs';
fileNode(P8,
  'Collection helper tracking the active sort column for TrackDataGrid with CycleSort and ApplySort methods that update column SortDirection and raise a SortChanged event.',
  ['utility', 'component', 'sorting'], 'simple');
classNode(P8, 'TrackDataGridColumns', [12, 53],
  'Manages sorted-column state across a TrackDataGrid column set: cycles ascending/descending/none and notifies consumers via SortChanged.',
  ['utility', 'sorting'], 'simple');

// ---- TrackDataGridDefaults.cs ----
const P9 = 'src/Wavee.UI.WinUI/Controls/TrackDataGrid/TrackDataGridDefaults.cs';
fileNode(P9,
  'Factory providing preconfigured column sets for each page context (playlist, album, liked songs, podcast), with named page key constants and individual column builder methods.',
  ['factory', 'component', 'track-grid'], 'moderate');
classNode(P9, 'TrackDataGridDefaults', [11, 185],
  'Static factory for page-specific default TrackDataGrid columns; dispatches via page key constants to per-context builders (playlist/album/liked/podcast).',
  ['factory', 'data-model'], 'moderate');
funcNode(P9, 'TrackDataGridDefaults', 'Create', [18, 27],
  'Selects and returns the default column array for a given page key, dispatching to BuildPlaylistColumns, BuildAlbumColumns, BuildLikedColumns, or BuildPodcastColumns.',
  ['factory'], 'simple');

// ---- TrackDataGridGroupRow.cs ----
const P10 = 'src/Wavee.UI.WinUI/Controls/TrackDataGrid/TrackDataGridGroupRow.cs';
fileNode(P10,
  'Simple record representing a group header sentinel in the flat TrackDataGrid row list, carrying header label and formatted item count.',
  ['data-model', 'component', 'track-grid'], 'simple');

// ---- TrackDataGridItemTemplateSelector.cs ----
const P11 = 'src/Wavee.UI.WinUI/Controls/TrackDataGrid/TrackDataGridItemTemplateSelector.cs';
fileNode(P11,
  'DataTemplateSelector returning the group header template for TrackDataGridGroupRow items and the track row template for ITrackItem instances.',
  ['component', 'factory', 'track-grid'], 'simple');

// ---- TrackDataGridSortDirection.cs ----
const P12 = 'src/Wavee.UI.WinUI/Controls/TrackDataGrid/TrackDataGridSortDirection.cs';
fileNode(P12,
  'Enum defining three sort direction values (None, Ascending, Descending) used by TrackDataGrid column sort state.',
  ['data-model', 'type-definition', 'sorting'], 'simple');

// ---- TrackSelectionBar.xaml ----
const P13 = 'src/Wavee.UI.WinUI/Controls/TrackDataGrid/TrackSelectionBar.xaml';
fileNode(P13,
  'XAML template for the multi-track selection action bar with Play, Play Next, Add to Queue, Like/Unlike, Remove, and Select All buttons.',
  ['component', 'markup', 'selection'], 'moderate');

// ---- TrackSelectionBar.xaml.cs ----
const P14 = 'src/Wavee.UI.WinUI/Controls/TrackDataGrid/TrackSelectionBar.xaml.cs';
fileNode(P14,
  'Code-behind for TrackSelectionBar exposing Play/PlayNext/AddToQueue/ToggleLike/Remove commands, selected count, and can-remove flag as DPs; subscribes to TrackDataGrid selection-mode events.',
  ['component', 'event-handler', 'selection'], 'complex');
classNode(P14, 'TrackSelectionBar', [14, 240],
  'WinUI 3 toolbar for multi-track selection mode; binds action buttons to selection commands and reflects current selection count and like state from TrackDataGrid.',
  ['component', 'selection', 'event-handler'], 'complex');

// ---- TrackListColumnDefinition.cs ----
const P15 = 'src/Wavee.UI.WinUI/Controls/TrackList/TrackListColumnDefinition.cs';
fileNode(P15,
  'Dependency-property column descriptor for TrackListView defining key, label, width, minimum width, visibility, and sort key for XAML binding.',
  ['data-model', 'component', 'track-grid'], 'moderate');
classNode(P15, 'TrackListColumnDefinition', [1, 82],
  'Column definition DependencyObject for TrackListView with observable key, label, width, and visibility properties.',
  ['data-model', 'component'], 'moderate');

// ---- TrackListView.xaml ----
const P16 = 'src/Wavee.UI.WinUI/Controls/TrackList/TrackListView.xaml';
fileNode(P16,
  'XAML control template for TrackListView providing a horizontally-scrollable virtualized track list with sticky group headers, inline loading skeleton rows, and a context-aware toolbar.',
  ['component', 'markup', 'track-grid'], 'complex');

// ---- TrackListView.xaml.cs ----
const P17 = 'src/Wavee.UI.WinUI/Controls/TrackList/TrackListView.xaml.cs';
fileNode(P17,
  'Code-behind for TrackListView: a horizontally-scrollable alternative to TrackDataGrid supporting grouping, sorting, filtering, density, selection mode, and drag-drop reorder.',
  ['component', 'track-grid', 'virtualization'], 'complex');
classNode(P17, 'TrackListView', [1, 1258],
  'WinUI 3 track listing control with horizontal-scroll layout and sticky group headers; shares structural patterns with TrackDataGrid but displays track art cards instead of tabular rows.',
  ['component', 'virtualization', 'track-grid'], 'complex');

// ---- WhatsNewDialog.xaml ----
const P18 = 'src/Wavee.UI.WinUI/Controls/WhatsNewDialog.xaml';
fileNode(P18,
  'XAML layout for the WhatsNew ContentDialog presenting a scrollable changelog list with version-tagged release notes and a dismiss button.',
  ['component', 'markup', 'dialog'], 'moderate');

// ---- WhatsNewDialog.xaml.cs ----
const P19 = 'src/Wavee.UI.WinUI/Controls/WhatsNewDialog.xaml.cs';
fileNode(P19,
  'Code-behind for WhatsNewDialog: loads changelog entries from resources, renders them in a ContentDialog, and persists the last-seen version to suppress duplicate showings.',
  ['component', 'dialog', 'event-handler'], 'moderate');
classNode(P19, 'WhatsNewDialog', [1, 147],
  'ContentDialog subclass that surfaces app update notes; reads changelog entries and tracks the seen version in app settings to prevent repeated display.',
  ['component', 'dialog'], 'moderate');

// ---- ZoomContentControl.cs ----
const P20 = 'src/Wavee.UI.WinUI/Controls/ZoomContentControl.cs';
fileNode(P20,
  'Custom WinUI 3 ContentControl applying a configurable zoom scale transform to its content, used for smooth density/compact transitions.',
  ['component', 'utility'], 'simple');
classNode(P20, 'ZoomContentControl', [1, 65],
  'ContentControl subclass with a ZoomFactor dependency property driving a ScaleTransform on the content presenter for density animations.',
  ['component', 'utility'], 'simple');

// ---- BoolToBackgroundConverter.cs ----
const P21 = 'src/Wavee.UI.WinUI/Converters/BoolToBackgroundConverter.cs';
fileNode(P21,
  'IValueConverter mapping a boolean to one of two configurable Brush values for conditional background binding.',
  ['utility', 'component', 'serialization'], 'simple');

// ---- BoolToFollowTextConverter.cs ----
const P22 = 'src/Wavee.UI.WinUI/Converters/BoolToFollowTextConverter.cs';
fileNode(P22,
  'Two IValueConverters: BoolToFollowTextConverter returns localized follow/unfollow strings and BoolToFollowGlyphConverter returns matching Fluent glyph codepoints.',
  ['utility', 'component', 'serialization'], 'simple');

// ---- BoolToGridListGlyphConverter.cs ----
const P23 = 'src/Wavee.UI.WinUI/Converters/BoolToGridListGlyphConverter.cs';
fileNode(P23,
  'IValueConverter mapping a boolean to grid-view or list-view Fluent glyph constants for view-toggle button icons.',
  ['utility', 'component', 'serialization'], 'simple');

// ---- BoolToHeartGlyphConverter.cs ----
const P24 = 'src/Wavee.UI.WinUI/Converters/BoolToHeartGlyphConverter.cs';
fileNode(P24,
  'IValueConverter mapping a liked/unliked boolean to the filled or outline heart Fluent glyph codepoint.',
  ['utility', 'component', 'serialization'], 'simple');

// ---- BoolToVisibilityConverter.cs ----
const P25 = 'src/Wavee.UI.WinUI/Converters/BoolToVisibilityConverter.cs';
fileNode(P25,
  'IValueConverter mapping a boolean to Visibility.Visible or Collapsed with an optional IsInverted dependency property.',
  ['utility', 'component', 'validation'], 'simple');

console.log('nodes:', nodes.length, 'edges:', edges.length);

const output = { nodes, edges };
fs.writeFileSync(
  'C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-37.json',
  JSON.stringify(output, null, 2)
);
console.log('Written batch-37.json');
