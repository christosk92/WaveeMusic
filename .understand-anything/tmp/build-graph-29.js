const fs = require('fs');
const data = JSON.parse(fs.readFileSync('C:/WAVEE/WaveeMusic/.understand-anything/tmp/ua-file-extract-results-29.json', 'utf8'));

const nodes = [];
const edges = [];

function fileNode(path, summary, tags, complexity, languageNotes) {
  const name = path.split('/').pop();
  const n = {id: 'file:' + path, type: 'file', name, filePath: path, summary, tags, complexity};
  if (languageNotes) n.languageNotes = languageNotes;
  return n;
}
function classNode(path, cls, summary, tags, complexity) {
  return {id: 'class:' + path + ':' + cls.name, type: 'class', name: cls.name, filePath: path, lineRange: [cls.startLine, cls.endLine], summary, tags, complexity};
}
function funcNode(path, fn, summary, tags, complexity) {
  return {id: 'function:' + path + ':' + fn.name, type: 'function', name: fn.name, filePath: path, lineRange: [fn.startLine, fn.endLine], summary, tags, complexity};
}
function containsEdge(filePath, nodeId) {
  return {source: 'file:' + filePath, target: nodeId, type: 'contains', direction: 'forward', weight: 1.0};
}
function exportsEdge(filePath, nodeId) {
  return {source: 'file:' + filePath, target: nodeId, type: 'exports', direction: 'forward', weight: 0.8};
}

// 1. ColumnsFirstGridLayout.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/Layouts/ColumnsFirstGridLayout.cs';
  const r = data.results.find(x => x.path === p);
  nodes.push(fileNode(p, 'Custom non-virtualizing WinUI layout that arranges children in a fixed number of columns, filling column-first before wrapping to the next row.', ['component','layout','custom-layout','winui'], 'moderate'));
  const cls = r.classes[0];
  nodes.push(classNode(p, cls, 'VirtualizingLayout subclass implementing column-first grid arrangement with configurable column count and spacing.', ['component','layout','custom-layout'], 'moderate'));
  edges.push(containsEdge(p, 'class:' + p + ':' + cls.name));
  edges.push(exportsEdge(p, 'class:' + p + ':' + cls.name));
  r.functions.forEach(fn => {
    const lineCount = fn.endLine - fn.startLine;
    if (lineCount < 10) return;
    let sum = '', tags = [];
    if (fn.name === 'MeasureOverride') { sum = 'Measures each child item and computes total layout size for column-first grid arrangement.'; tags = ['layout','measurement']; }
    else if (fn.name === 'ArrangeOverride') { sum = 'Arranges children into column-first grid positions after measurement pass.'; tags = ['layout','arrangement']; }
    else return;
    nodes.push(funcNode(p, fn, sum, tags, 'moderate'));
    edges.push(containsEdge(p, 'function:' + p + ':' + fn.name));
  });
}

// 2. NonVirtualizingStackLayout.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/Layouts/NonVirtualizingStackLayout.cs';
  const r = data.results.find(x => x.path === p);
  nodes.push(fileNode(p, 'Non-virtualizing stack layout for WinUI that arranges children vertically or horizontally without item recycling, useful for small collections.', ['component','layout','stack-layout','winui'], 'moderate'));
  const cls = r.classes[0];
  nodes.push(classNode(p, cls, 'NonVirtualizingLayout subclass providing simple sequential arrangement with configurable spacing and orientation.', ['component','layout','stack-layout'], 'moderate'));
  edges.push(containsEdge(p, 'class:' + p + ':' + cls.name));
  edges.push(exportsEdge(p, 'class:' + p + ':' + cls.name));
  r.functions.forEach(fn => {
    const lineCount = fn.endLine - fn.startLine;
    if (lineCount < 10) return;
    let sum = '', tags = [];
    if (fn.name === 'MeasureOverride') { sum = 'Measures children sequentially and computes stack layout size in the primary axis direction.'; tags = ['layout','measurement']; }
    else if (fn.name === 'ArrangeOverride') { sum = 'Arranges children one after another along the stack axis respecting spacing.'; tags = ['layout','arrangement']; }
    else return;
    nodes.push(funcNode(p, fn, sum, tags, 'simple'));
    edges.push(containsEdge(p, 'function:' + p + ':' + fn.name));
  });
}

// 3. SafeUniformGridLayout.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/Layouts/SafeUniformGridLayout.cs';
  const r = data.results.find(x => x.path === p);
  nodes.push(fileNode(p, 'Defensive wrapper around WinUI uniform grid layout that guards against NaN/infinity/negative measure inputs, preventing layout crashes on dynamic resize.', ['component','layout','grid-layout','winui','defensive-programming'], 'complex'));
  const cls = r.classes[0];
  nodes.push(classNode(p, cls, 'VirtualizingLayout subclass implementing uniform grid with safe arithmetic guards for all measurement and arrangement operations.', ['component','layout','grid-layout','defensive-programming'], 'complex'));
  edges.push(containsEdge(p, 'class:' + p + ':' + cls.name));
  edges.push(exportsEdge(p, 'class:' + p + ':' + cls.name));
  r.functions.forEach(fn => {
    const lineCount = fn.endLine - fn.startLine;
    if (lineCount < 10) return;
    let sum = '', tags = [];
    if (fn.name === 'MeasureOverride') { sum = 'Measures children in a uniform grid, clipping or clamping invalid dimensions before passing to child measure.'; tags = ['layout','measurement','defensive-programming']; }
    else if (fn.name === 'ArrangeOverride') { sum = 'Arranges children in a uniform grid, applying safe bounds before each item placement.'; tags = ['layout','arrangement']; }
    else if (fn.name === 'GetRealizedRowRange') { sum = 'Computes the range of rows that are currently realized in the viewport for virtualization support.'; tags = ['layout','virtualization']; }
    else return;
    nodes.push(funcNode(p, fn, sum, tags, 'moderate'));
    edges.push(containsEdge(p, 'function:' + p + ':' + fn.name));
  });
}

// 4. SectionStackLayout.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/Layouts/SectionStackLayout.cs';
  const r = data.results.find(x => x.path === p);
  nodes.push(fileNode(p, 'WinUI virtualizing layout that stacks heterogeneous sections with per-section spacing and caches measurement results to optimize incremental layout passes.', ['component','layout','section-layout','virtualizing','winui'], 'complex'));
  const cls = r.classes[0];
  nodes.push(classNode(p, cls, 'VirtualizingLayout that stacks named sections with configurable spacing, maintaining a measurement cache keyed by section identity.', ['component','layout','section-layout','virtualizing'], 'complex'));
  edges.push(containsEdge(p, 'class:' + p + ':' + cls.name));
  edges.push(exportsEdge(p, 'class:' + p + ':' + cls.name));
  r.functions.forEach(fn => {
    const lineCount = fn.endLine - fn.startLine;
    if (lineCount < 10) return;
    let sum = '', tags = [];
    if (fn.name === 'MeasureOverride') { sum = 'Measures all sections, rebuilding the cache when layout inputs changed since last pass.'; tags = ['layout','measurement','caching']; }
    else if (fn.name === 'ArrangeOverride') { sum = 'Arranges sections in vertical stack order using cached measurements.'; tags = ['layout','arrangement']; }
    else if (fn.name === 'ResetCache') { sum = 'Invalidates the per-section measurement cache, triggering a full remeasure on next layout pass.'; tags = ['layout','caching']; }
    else return;
    nodes.push(funcNode(p, fn, sum, tags, 'complex'));
    edges.push(containsEdge(p, 'function:' + p + ':' + fn.name));
  });
}

// 5. ShortcutsGridLayout.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/Layouts/ShortcutsGridLayout.cs';
  const r = data.results.find(x => x.path === p);
  nodes.push(fileNode(p, 'Custom WinUI grid layout for the Home shortcuts grid, computing column count and cell sizes based on available width and a configurable preferred size.', ['component','layout','grid-layout','home-page','winui'], 'moderate'));
  const cls = r.classes[0];
  nodes.push(classNode(p, cls, 'NonVirtualizingLayout for Home shortcut cards, auto-sizing columns from available width.', ['component','layout','home-page'], 'moderate'));
  edges.push(containsEdge(p, 'class:' + p + ':' + cls.name));
  edges.push(exportsEdge(p, 'class:' + p + ':' + cls.name));
  r.functions.forEach(fn => {
    const lineCount = fn.endLine - fn.startLine;
    if (lineCount < 10) return;
    let sum = '', tags = [];
    if (fn.name === 'MeasureOverride') { sum = 'Determines column count from available width and measures each shortcut card to a uniform cell size.'; tags = ['layout','measurement']; }
    else if (fn.name === 'ArrangeOverride') { sum = 'Positions shortcut cards in a grid using the column count computed during measure.'; tags = ['layout','arrangement']; }
    else return;
    nodes.push(funcNode(p, fn, sum, tags, 'moderate'));
    edges.push(containsEdge(p, 'function:' + p + ':' + fn.name));
  });
}

// 6. SingleRowLayout.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/Layouts/SingleRowLayout.cs';
  const r = data.results.find(x => x.path === p);
  nodes.push(fileNode(p, 'WinUI layout that arranges all children in a single horizontal row with uniform item widths, useful for horizontally scrolling shelves.', ['component','layout','horizontal-layout','shelf','winui'], 'moderate'));
  const cls = r.classes[0];
  nodes.push(classNode(p, cls, 'NonVirtualizingLayout placing all children in one row at equal widths, designed for horizontal shelf or carousel contexts.', ['component','layout','shelf'], 'moderate'));
  edges.push(containsEdge(p, 'class:' + p + ':' + cls.name));
  edges.push(exportsEdge(p, 'class:' + p + ':' + cls.name));
  r.functions.forEach(fn => {
    const lineCount = fn.endLine - fn.startLine;
    if (lineCount < 10) return;
    let sum = '', tags = [];
    if (fn.name === 'MeasureOverride') { sum = 'Measures all children at a uniform width derived from available space divided by item count.'; tags = ['layout','measurement']; }
    else if (fn.name === 'ArrangeOverride') { sum = 'Arranges children sequentially in a single horizontal row.'; tags = ['layout','arrangement']; }
    else return;
    nodes.push(funcNode(p, fn, sum, tags, 'simple'));
    edges.push(containsEdge(p, 'function:' + p + ':' + fn.name));
  });
}

// 7. LibrarySortViewPanel.xaml
{
  const p = 'src/Wavee.UI.WinUI/Controls/Library/LibrarySortViewPanel.xaml';
  nodes.push(fileNode(p, 'XAML template for the library sort/view-mode panel, defining sort option toggle buttons, view-mode radio buttons, and grid-scale slider affordances.', ['component','library','sort-control','xaml','view-mode'], 'complex'));
}

// 8. LibrarySortViewPanel.xaml.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/Library/LibrarySortViewPanel.xaml.cs';
  const r = data.results.find(x => x.path === p);
  nodes.push(fileNode(p, 'Code-behind for the library sort/view-mode panel UserControl, wiring sort-key toggle buttons, view-mode radio buttons, and grid-scale visibility to dependency-property callbacks.', ['component','library','sort-control','event-handler','dependency-property'], 'complex'));
  const cls = r.classes[0];
  nodes.push(classNode(p, cls, 'UserControl that exposes SortBy, SortDirection, ViewMode, AllowedSortKeys, and ShowGridScale dependency properties and drives the sort panel UI state.', ['component','library','sort-control','dependency-property'], 'complex'));
  edges.push(containsEdge(p, 'class:' + p + ':' + cls.name));
  edges.push(exportsEdge(p, 'class:' + p + ':' + cls.name));
  const sigFuncs = [
    {name: 'ApplyViewModeToUi', sum: 'Updates visual state and toggle selection to reflect the current ViewMode dependency-property value.', tags: ['event-handler','view-mode']},
    {name: 'UpdateTriggerDisplay', sum: 'Rebuilds the trigger label displayed in the sort panel header summarizing active sort and direction.', tags: ['ui-logic','display']},
    {name: 'ApplyAllowedSortKeys', sum: 'Shows or hides individual sort option rows based on the AllowedSortKeys collection.', tags: ['ui-logic','filtering']},
    {name: 'SortOption_Click', sum: 'Handles a sort option toggle click, updating SortBy and SortDirection dependency properties and raising change events.', tags: ['event-handler','sort-control']},
    {name: 'ViewToggle_Click', sum: 'Handles view-mode radio toggle click, updating the ViewMode dependency property.', tags: ['event-handler','view-mode']},
    {name: 'GetAllowedKeys', sum: 'Parses the AllowedSortKeys string or collection into a concrete set of allowed key identifiers.', tags: ['utility','sort-control']},
    {name: 'EnumerateSortRows', sum: 'Enumerates the XAML sort-option row elements for bulk show/hide operations.', tags: ['utility','ui-logic']},
  ];
  sigFuncs.forEach(sf => {
    const fn = r.functions.find(f => f.name === sf.name);
    if (!fn) return;
    const lineCount = fn.endLine - fn.startLine;
    if (lineCount < 10) return;
    nodes.push(funcNode(p, fn, sf.sum, sf.tags, 'moderate'));
    edges.push(containsEdge(p, 'function:' + p + ':' + fn.name));
  });
}

// 9. LibraryGridView.xaml
{
  const p = 'src/Wavee.UI.WinUI/Controls/LibraryGridView/LibraryGridView.xaml';
  nodes.push(fileNode(p, 'XAML template for the LibraryGridView control, defining grid/list view switcher, shimmer loading overlay, and ItemsRepeater-based grid and list item templates.', ['component','library','grid-view','xaml','loading-state'], 'moderate'));
}

// 10. LibraryGridView.xaml.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/LibraryGridView/LibraryGridView.xaml.cs';
  const r = data.results.find(x => x.path === p);
  nodes.push(fileNode(p, 'Code-behind for LibraryGridView, a composite control switching between grid and list layouts with shimmer loading, selection sync, collection-change tracking, and search filtering.', ['component','library','grid-view','event-handler','selection','loading-state'], 'complex'));
  const cls = r.classes[0];
  nodes.push(classNode(p, cls, 'UserControl that hosts ItemsRepeater with grid/list view switching, shimmer overlay, selection management, and incremental loading support for the user library.', ['component','library','grid-view','selection'], 'complex'));
  edges.push(containsEdge(p, 'class:' + p + ':' + cls.name));
  edges.push(exportsEdge(p, 'class:' + p + ':' + cls.name));
  const sigFuncs = [
    {name: 'ApplyViewMode', sum: 'Switches the ItemsRepeater layout between grid and list modes and updates item templates accordingly.', tags: ['ui-logic','view-mode','layout']},
    {name: 'SyncSelectionToItemsView', sum: 'Propagates the SelectedItem dependency property into the ItemsRepeater visual selection state.', tags: ['selection','data-binding']},
    {name: 'ItemsSource_CollectionChanged', sum: 'Handles collection-change notifications on the items source to keep the repeater in sync and update loading state.', tags: ['event-handler','collection','data-binding']},
    {name: 'ApplyLoadingVisualState', sum: 'Transitions the shimmer/content visual state based on the IsLoading dependency property.', tags: ['ui-logic','loading-state']},
    {name: 'OnSearchQueryChanged', sum: 'Reacts to search query changes, filtering the displayed library items.', tags: ['filtering','search']},
  ];
  sigFuncs.forEach(sf => {
    const fn = r.functions.find(f => f.name === sf.name);
    if (!fn) return;
    const lineCount = fn.endLine - fn.startLine;
    if (lineCount < 10) return;
    nodes.push(funcNode(p, fn, sf.sum, sf.tags, 'moderate'));
    edges.push(containsEdge(p, 'function:' + p + ':' + fn.name));
  });
}

// 11. LinkSpotifyTrackFlyout.xaml
{
  const p = 'src/Wavee.UI.WinUI/Controls/Local/LinkSpotifyTrackFlyout.xaml';
  nodes.push(fileNode(p, 'XAML template for the Spotify track link flyout, defining search box, results list, preview panel, shimmer, and state-transition panels for linking local files to Spotify tracks.', ['component','local-media','flyout','search','xaml'], 'complex'));
}

// 12. LinkSpotifyTrackFlyout.xaml.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/Local/LinkSpotifyTrackFlyout.xaml.cs';
  const r = data.results.find(x => x.path === p);
  nodes.push(fileNode(p, 'Code-behind for LinkSpotifyTrackFlyout: implements full search-as-you-type, debounced Spotify track lookup, animated state machine (idle/shimmer/results/preview/empty/error), and persists the link selection.', ['component','local-media','flyout','search','animation','state-machine'], 'complex'));
  const cls = r.classes[0];
  nodes.push(classNode(p, cls, 'Flyout UserControl managing debounced search, result reconciliation, preview resolution, animated panel transitions, and track-link persistence for local media files.', ['component','local-media','search','animation','state-machine'], 'complex'));
  edges.push(containsEdge(p, 'class:' + p + ':' + cls.name));
  edges.push(exportsEdge(p, 'class:' + p + ':' + cls.name));
  const sigFuncs = [
    {name: 'EvaluateCurrentQuery', sum: 'Determines whether to trigger a new search or clear results based on current query text, debounce state, and minimum length threshold.', tags: ['search','debouncing','ui-logic']},
    {name: 'SearchAsync', sum: 'Executes the Spotify track search API call and populates results, cancelling any in-flight previous search.', tags: ['search','async','api-handler']},
    {name: 'ReconcileResults', sum: 'Diffing pass that updates the displayed results collection to match the fresh search response while preserving scroll position.', tags: ['search','ui-logic','reconciliation']},
    {name: 'ResolvePreviewAsync', sum: 'Fetches full track metadata for the selected result to populate the preview panel.', tags: ['api-handler','async','preview']},
    {name: 'BuildPreview', sum: 'Constructs the preview panel view model from resolved track metadata including artwork, title, artist, and duration.', tags: ['preview','data-model']},
    {name: 'ApplyPreview', sum: 'Binds the built preview model to the preview panel UI elements.', tags: ['preview','data-binding']},
    {name: 'TransitionTo', sum: 'Animated state-machine dispatcher that crossfades between idle, shimmer, results, preview, empty, and error panels.', tags: ['animation','state-machine','ui-logic']},
    {name: 'HideShimmer', sum: 'Fades out the shimmer loading overlay with a minimum-show-time guard to prevent flash.', tags: ['animation','loading-state']},
    {name: 'EnsureShimmerMinShowAsync', sum: 'Guarantees the shimmer panel is visible for at least a minimum duration before allowing hide.', tags: ['animation','loading-state','async']},
    {name: 'LinkAsync', sum: 'Persists the user-confirmed Spotify track link for the local media file and closes the flyout.', tags: ['local-media','persistence','async']},
    {name: 'QueryBox_KeyDown', sum: 'Handles keyboard shortcuts in the search box including Enter to confirm and Escape to cancel.', tags: ['event-handler','keyboard','search']},
    {name: 'ShowResultsHost', sum: 'Animates the results list panel into view with fade/slide entrance.', tags: ['animation','ui-logic']},
    {name: 'HideStrip', sum: 'Animates the matched-result strip out of view after confirmation or cancellation.', tags: ['animation','ui-logic']},
  ];
  sigFuncs.forEach(sf => {
    const fn = r.functions.find(f => f.name === sf.name);
    if (!fn) return;
    const lineCount = fn.endLine - fn.startLine;
    if (lineCount < 10) return;
    nodes.push(funcNode(p, fn, sf.sum, sf.tags, 'moderate'));
    edges.push(containsEdge(p, 'function:' + p + ':' + fn.name));
  });
}

// 13. LocalEpisodeCard.xaml
{
  const p = 'src/Wavee.UI.WinUI/Controls/Local/LocalEpisodeCard.xaml';
  nodes.push(fileNode(p, 'XAML template for LocalEpisodeCard, a card-format UI for displaying local podcast episodes with thumbnail, title, duration, and progress indicator.', ['component','local-media','episode','card','xaml'], 'moderate'));
}

// 14. LocalEpisodeCard.xaml.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/Local/LocalEpisodeCard.xaml.cs';
  const r = data.results.find(x => x.path === p);
  nodes.push(fileNode(p, 'Code-behind for LocalEpisodeCard, wiring playback state tracking, pointer hover scale/opacity animations, resume-fill progress arc, and play/context-menu click handling for local podcast episodes.', ['component','local-media','episode','card','animation','event-handler'], 'complex'));
  const cls = r.classes[0];
  nodes.push(classNode(p, cls, 'UserControl card for a local episode item with scale/opacity hover animations, playback-state reactivity, and resume-progress arc rendering.', ['component','local-media','episode','card','animation'], 'complex'));
  edges.push(containsEdge(p, 'class:' + p + ':' + cls.name));
  edges.push(exportsEdge(p, 'class:' + p + ':' + cls.name));
  const sigFuncs = [
    {name: 'Apply', sum: 'Populates all visual fields (thumbnail, title, duration, progress arc) from the bound episode view model.', tags: ['data-binding','ui-logic']},
    {name: 'RefreshPlayingState', sum: 'Reacts to playback state changes to toggle now-playing indicator and animate the card accordingly.', tags: ['playback','animation']},
    {name: 'LocalEpisodeCard_Loaded', sum: 'Subscribes to playback state observable and syncs initial visual state on load.', tags: ['lifecycle','playback']},
    {name: 'LocalEpisodeCard_Unloaded', sum: 'Disposes playback state subscription and stops any running animations on unload.', tags: ['lifecycle','cleanup']},
    {name: 'ScaleCard', sum: 'Animates the card scale on pointer enter/exit for the hover affordance.', tags: ['animation','pointer']},
    {name: 'AnimateOpacity', sum: 'Fades card elements in/out during hover or play state transitions.', tags: ['animation','opacity']},
    {name: 'CardButton_Click', sum: 'Handles primary click on the card, initiating episode playback via the view model.', tags: ['event-handler','playback']},
  ];
  sigFuncs.forEach(sf => {
    const fn = r.functions.find(f => f.name === sf.name);
    if (!fn) return;
    const lineCount = fn.endLine - fn.startLine;
    if (lineCount < 10) return;
    nodes.push(funcNode(p, fn, sf.sum, sf.tags, 'moderate'));
    edges.push(containsEdge(p, 'function:' + p + ':' + fn.name));
  });
}

// 15. LocalEpisodeRow.xaml
{
  const p = 'src/Wavee.UI.WinUI/Controls/Local/LocalEpisodeRow.xaml';
  nodes.push(fileNode(p, 'XAML template for LocalEpisodeRow, a row-format UI element for local podcast episodes in list view with thumbnail, title, metadata, and progress.', ['component','local-media','episode','row','xaml'], 'moderate'));
}

// 16. LocalEpisodeRow.xaml.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/Local/LocalEpisodeRow.xaml.cs';
  const r = data.results.find(x => x.path === p);
  nodes.push(fileNode(p, 'Code-behind for LocalEpisodeRow, binding episode metadata to row UI elements and handling tap/right-tap interactions for list-view local podcast episode items.', ['component','local-media','episode','row','event-handler'], 'moderate'));
  const cls = r.classes[0];
  nodes.push(classNode(p, cls, 'UserControl row item for local episode list view, populating thumbnail, title, duration, and wiring tap/context-menu handlers.', ['component','local-media','episode','row'], 'moderate'));
  edges.push(containsEdge(p, 'class:' + p + ':' + cls.name));
  edges.push(exportsEdge(p, 'class:' + p + ':' + cls.name));
  const fn = r.functions.find(f => f.name === 'Apply');
  if (fn && (fn.endLine - fn.startLine) >= 10) {
    nodes.push(funcNode(p, fn, 'Populates all row UI fields from the bound local episode view model including thumbnail, title, show, date, and duration.', ['data-binding','ui-logic'], 'complex'));
    edges.push(containsEdge(p, 'function:' + p + ':Apply'));
  }
  const fnAnim = r.functions.find(f => f.name === 'AnimateOpacity');
  if (fnAnim && (fnAnim.endLine - fnAnim.startLine) >= 10) {
    nodes.push(funcNode(p, fnAnim, 'Animates row opacity on pointer enter/exit hover transitions.', ['animation','pointer'], 'simple'));
    edges.push(containsEdge(p, 'function:' + p + ':AnimateOpacity'));
  }
}

// 17. LocalItemDetailFlyout.xaml
{
  const p = 'src/Wavee.UI.WinUI/Controls/Local/LocalItemDetailFlyout.xaml';
  nodes.push(fileNode(p, 'XAML template for LocalItemDetailFlyout, presenting editable metadata fields and a drag-drop target for local media item enrichment.', ['component','local-media','flyout','drag-drop','xaml'], 'moderate'));
}

// 18. LocalItemDetailFlyout.xaml.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/Local/LocalItemDetailFlyout.xaml.cs';
  const r = data.results.find(x => x.path === p);
  nodes.push(fileNode(p, 'Code-behind for LocalItemDetailFlyout, handling async load of item metadata, save action, and drag-drop of image files onto the local media detail editor.', ['component','local-media','flyout','drag-drop','async'], 'moderate'));
  const cls = r.classes[0];
  nodes.push(classNode(p, cls, 'Flyout control for editing local media item metadata, supporting drag-drop image import and async save.', ['component','local-media','flyout','drag-drop'], 'moderate'));
  edges.push(containsEdge(p, 'class:' + p + ':' + cls.name));
  edges.push(exportsEdge(p, 'class:' + p + ':' + cls.name));
  const fnDrop = r.functions.find(f => f.name === 'Root_Drop');
  if (fnDrop && (fnDrop.endLine - fnDrop.startLine) >= 10) {
    nodes.push(funcNode(p, fnDrop, 'Handles the drag-drop operation, reading the dropped file and applying it as the local item artwork.', ['drag-drop','event-handler','local-media'], 'moderate'));
    edges.push(containsEdge(p, 'function:' + p + ':Root_Drop'));
  }
}

// 19. UpNextEpisodeOverlay.xaml
{
  const p = 'src/Wavee.UI.WinUI/Controls/Local/UpNextEpisodeOverlay.xaml';
  nodes.push(fileNode(p, 'XAML template for UpNextEpisodeOverlay, an animated overlay card shown when an episode ends with poster, title, and Watch Now / Cancel actions.', ['component','local-media','episode','overlay','animation','xaml'], 'moderate'));
}

// 20. UpNextEpisodeOverlay.xaml.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/Local/UpNextEpisodeOverlay.xaml.cs';
  const r = data.results.find(x => x.path === p);
  nodes.push(fileNode(p, 'Code-behind for UpNextEpisodeOverlay, syncing poster image and episode metadata from the view model and driving slide-in/out visibility animations.', ['component','local-media','episode','overlay','animation','event-handler'], 'moderate'));
  const cls = r.classes[0];
  nodes.push(classNode(p, cls, 'Overlay UserControl that presents the up-next episode with animated visibility and binds to a view-model via dependency property change callbacks.', ['component','local-media','episode','overlay','animation'], 'moderate'));
  edges.push(containsEdge(p, 'class:' + p + ':' + cls.name));
  edges.push(exportsEdge(p, 'class:' + p + ':' + cls.name));
  const fnAnim = r.functions.find(f => f.name === 'AnimateVisibility');
  if (fnAnim && (fnAnim.endLine - fnAnim.startLine) >= 10) {
    nodes.push(funcNode(p, fnAnim, 'Drives the slide-in or slide-out animation for the overlay based on the desired visibility state.', ['animation','visibility'], 'complex'));
    edges.push(containsEdge(p, 'function:' + p + ':AnimateVisibility'));
  }
  const fnApplyPoster = r.functions.find(f => f.name === 'ApplyPoster');
  if (fnApplyPoster && (fnApplyPoster.endLine - fnApplyPoster.startLine) >= 10) {
    nodes.push(funcNode(p, fnApplyPoster, 'Loads and applies the episode poster image to the overlay, handling async image source resolution.', ['data-binding','image-loading'], 'moderate'));
    edges.push(containsEdge(p, 'function:' + p + ':ApplyPoster'));
  }
  const fnSync = r.functions.find(f => f.name === 'SyncFromViewModel');
  if (fnSync && (fnSync.endLine - fnSync.startLine) >= 10) {
    nodes.push(funcNode(p, fnSync, 'Copies all relevant fields from the view model into the overlay UI elements.', ['data-binding','ui-logic'], 'simple'));
    edges.push(containsEdge(p, 'function:' + p + ':SyncFromViewModel'));
  }
}

// 21. LocationButton.xaml
{
  const p = 'src/Wavee.UI.WinUI/Controls/LocationButton.xaml';
  nodes.push(fileNode(p, 'XAML template for LocationButton, a small button that opens a folder/path picker dialog to select a local media directory.', ['component','local-media','button','xaml'], 'simple'));
}

// 22. LocationButton.xaml.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/LocationButton.xaml.cs';
  const r = data.results.find(x => x.path === p);
  nodes.push(fileNode(p, 'Code-behind for LocationButton, a button UserControl that opens the LocationPickerDialog and exposes the selected path via a dependency property.', ['component','local-media','button','event-handler'], 'simple'));
  const cls = r.classes[0];
  nodes.push(classNode(p, cls, 'Button control that triggers a folder picker dialog and exposes the selected path as a Path dependency property.', ['component','local-media','button'], 'simple'));
  edges.push(containsEdge(p, 'class:' + p + ':' + cls.name));
  edges.push(exportsEdge(p, 'class:' + p + ':' + cls.name));
  const fnClick = r.functions.find(f => f.name === 'OnClick');
  if (fnClick && (fnClick.endLine - fnClick.startLine) >= 10) {
    nodes.push(funcNode(p, fnClick, 'Opens the LocationPickerDialog and writes the user-selected path into the Path dependency property.', ['event-handler','local-media'], 'simple'));
    edges.push(containsEdge(p, 'function:' + p + ':OnClick'));
  }
}

// 23. LocationPickerDialog.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/LocationPickerDialog.cs';
  const r = data.results.find(x => x.path === p);
  nodes.push(fileNode(p, 'ContentDialog wrapper that presents a folder picker for selecting local media root directories, validating the chosen path before confirming.', ['component','local-media','dialog','file-picker'], 'moderate'));
  const cls = r.classes[0];
  nodes.push(classNode(p, cls, 'ContentDialog subclass that hosts a folder path picker UI and validates the user-chosen directory before allowing confirmation.', ['component','local-media','dialog','file-picker'], 'moderate'));
  edges.push(containsEdge(p, 'class:' + p + ':' + cls.name));
  edges.push(exportsEdge(p, 'class:' + p + ':' + cls.name));
  const fn = r.functions.find(f => f.name === 'ShowAsync');
  if (fn && (fn.endLine - fn.startLine) >= 10) {
    nodes.push(funcNode(p, fn, 'Shows the folder picker dialog asynchronously, returning the validated path or null on cancel.', ['async','file-picker','dialog'], 'complex'));
    edges.push(containsEdge(p, 'function:' + p + ':ShowAsync'));
  }
}

// 24. MiniVideoPlayer.xaml
{
  const p = 'src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/MiniVideoPlayer.xaml';
  nodes.push(fileNode(p, 'XAML template for MiniVideoPlayer, defining the floating compact video window with chrome bar, resize grips, close button, and video surface host.', ['component','video','mini-player','xaml','floating-window'], 'complex'));
}

// 25. MiniVideoPlayer.xaml.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/MiniVideoPlayer.xaml.cs';
  const r = data.results.find(x => x.path === p);
  nodes.push(fileNode(p, 'Code-behind for MiniVideoPlayer: floating compact video player with drag-to-move chrome bar, multi-grip corner resizing, auto-hide overlay controls, MediaPlayer or element surface attachment, and view-model-driven state synchronization.', ['component','video','mini-player','drag-drop','resize','animation','event-handler'], 'complex'));
  const cls = r.classes[0];
  nodes.push(classNode(p, cls, 'UserControl floating video player that manages surface attachment (MediaPlayer/ElementSurface), pointer-driven drag and multi-corner resize, auto-hide timer for chrome overlay, and reactive view-model binding.', ['component','video','mini-player','drag-drop','resize'], 'complex'));
  edges.push(containsEdge(p, 'class:' + p + ':' + cls.name));
  edges.push(exportsEdge(p, 'class:' + p + ':' + cls.name));
  const sigFuncs = [
    {name: 'OnLoaded', sum: 'Subscribes to video surface state and ownership observables and initializes the auto-hide timer on first load.', tags: ['lifecycle','video','reactive']},
    {name: 'OnUnloaded', sum: 'Disposes all observable subscriptions and detaches the active video surface on unload.', tags: ['lifecycle','cleanup']},
    {name: 'OnActiveVideoSurfaceStateChanged', sum: 'Reacts to video surface availability changes, attaching or detaching the MediaPlayer or element surface.', tags: ['video','reactive','surface']},
    {name: 'OnSurfaceOwnershipChanged', sum: 'Handles surface ownership transfer events, detaching the old surface and attaching the newly owned one.', tags: ['video','surface','reactive']},
    {name: 'AttachSurface', sum: 'Routes surface attachment to the appropriate method based on surface type (MediaPlayer vs element surface).', tags: ['video','surface']},
    {name: 'AttachElementSurface', sum: 'Attaches a composition element surface to the video host panel, setting up the visual swap chain.', tags: ['video','surface','composition']},
    {name: 'DetachSurface', sum: 'Cleans up the currently attached video surface without leaving dangling references.', tags: ['video','surface','cleanup']},
    {name: 'FadeOverlay', sum: 'Animates the chrome overlay opacity in or out for the auto-hide behaviour.', tags: ['animation','overlay']},
    {name: 'ChromeBar_PointerPressed', sum: 'Begins a window-drag operation when the user presses on the chrome bar.', tags: ['event-handler','drag','pointer']},
    {name: 'ChromeBar_PointerMoved', sum: 'Translates the floating window position during a chrome-bar drag operation.', tags: ['event-handler','drag','pointer']},
    {name: 'ResizeGrip_PointerPressed', sum: 'Begins a bottom-right corner resize operation for the floating video window.', tags: ['event-handler','resize','pointer']},
    {name: 'ResizeGrip_PointerMoved', sum: 'Adjusts window width and height during a bottom-right corner resize drag.', tags: ['event-handler','resize','pointer']},
    {name: 'OnViewModelPropertyChanged', sum: 'Reacts to view-model property changes (IsVisible, IsExpanded, etc.) and synchronizes the mini player visual state.', tags: ['reactive','data-binding','view-model']},
  ];
  sigFuncs.forEach(sf => {
    const fn = r.functions.find(f => f.name === sf.name);
    if (!fn) return;
    const lineCount = fn.endLine - fn.startLine;
    if (lineCount < 10) return;
    nodes.push(funcNode(p, fn, sf.sum, sf.tags, 'moderate'));
    edges.push(containsEdge(p, 'function:' + p + ':' + fn.name));
  });
}

const nodeCount = nodes.length;
const edgeCount = edges.length;
console.log('nodeCount:', nodeCount, 'edgeCount:', edgeCount);

const outDir = 'C:/WAVEE/WaveeMusic/.understand-anything/intermediate';

if (nodeCount <= 60 && edgeCount <= 120) {
  const out = {nodes, edges};
  fs.writeFileSync(outDir + '/batch-29.json', JSON.stringify(out), 'utf8');
  console.log('Wrote single file batch-29.json');
} else {
  const parts = Math.ceil(Math.max(nodeCount / 60, edgeCount / 120));
  console.log('parts:', parts);
  // Sort files alphabetically
  const filePaths = [
    'src/Wavee.UI.WinUI/Controls/Layouts/ColumnsFirstGridLayout.cs',
    'src/Wavee.UI.WinUI/Controls/Layouts/NonVirtualizingStackLayout.cs',
    'src/Wavee.UI.WinUI/Controls/Layouts/SafeUniformGridLayout.cs',
    'src/Wavee.UI.WinUI/Controls/Layouts/SectionStackLayout.cs',
    'src/Wavee.UI.WinUI/Controls/Layouts/ShortcutsGridLayout.cs',
    'src/Wavee.UI.WinUI/Controls/Layouts/SingleRowLayout.cs',
    'src/Wavee.UI.WinUI/Controls/Library/LibrarySortViewPanel.xaml',
    'src/Wavee.UI.WinUI/Controls/Library/LibrarySortViewPanel.xaml.cs',
    'src/Wavee.UI.WinUI/Controls/LibraryGridView/LibraryGridView.xaml',
    'src/Wavee.UI.WinUI/Controls/LibraryGridView/LibraryGridView.xaml.cs',
    'src/Wavee.UI.WinUI/Controls/Local/LinkSpotifyTrackFlyout.xaml',
    'src/Wavee.UI.WinUI/Controls/Local/LinkSpotifyTrackFlyout.xaml.cs',
    'src/Wavee.UI.WinUI/Controls/Local/LocalEpisodeCard.xaml',
    'src/Wavee.UI.WinUI/Controls/Local/LocalEpisodeCard.xaml.cs',
    'src/Wavee.UI.WinUI/Controls/Local/LocalEpisodeRow.xaml',
    'src/Wavee.UI.WinUI/Controls/Local/LocalEpisodeRow.xaml.cs',
    'src/Wavee.UI.WinUI/Controls/Local/LocalItemDetailFlyout.xaml',
    'src/Wavee.UI.WinUI/Controls/Local/LocalItemDetailFlyout.xaml.cs',
    'src/Wavee.UI.WinUI/Controls/Local/UpNextEpisodeOverlay.xaml',
    'src/Wavee.UI.WinUI/Controls/Local/UpNextEpisodeOverlay.xaml.cs',
    'src/Wavee.UI.WinUI/Controls/LocationButton.xaml',
    'src/Wavee.UI.WinUI/Controls/LocationButton.xaml.cs',
    'src/Wavee.UI.WinUI/Controls/LocationPickerDialog.cs',
    'src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/MiniVideoPlayer.xaml',
    'src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/MiniVideoPlayer.xaml.cs',
  ].sort();
  const chunkSize = Math.ceil(filePaths.length / parts);
  for (let k = 0; k < parts; k++) {
    const partFiles = new Set(filePaths.slice(k * chunkSize, (k + 1) * chunkSize));
    const partNodes = nodes.filter(n => !n.filePath || partFiles.has(n.filePath));
    const partNodeIds = new Set(partNodes.map(n => n.id));
    const partEdges = edges.filter(e => partNodeIds.has(e.source));
    const out = {nodes: partNodes, edges: partEdges};
    fs.writeFileSync(outDir + '/batch-29-part-' + (k+1) + '.json', JSON.stringify(out), 'utf8');
    console.log('Wrote batch-29-part-' + (k+1) + '.json nodes=' + partNodes.length + ' edges=' + partEdges.length);
  }
}
