const fs = require('fs');

const nodes = [];
const edges = [];

function fileNode(path, summary, tags, complexity, langNotes) {
  const name = path.split('/').pop();
  const n = { id: 'file:' + path, type: 'file', name, filePath: path, summary, tags, complexity };
  if (langNotes) n.languageNotes = langNotes;
  return n;
}
function classNode(path, name, lineRange, summary, tags, complexity) {
  return { id: 'class:' + path + ':' + name, type: 'class', name, filePath: path, lineRange, summary, tags, complexity };
}
function contains(filePath, nodeId) {
  return { source: 'file:' + filePath, target: nodeId, type: 'contains', direction: 'forward', weight: 1.0 };
}
function exportEdge(filePath, nodeId) {
  return { source: 'file:' + filePath, target: nodeId, type: 'exports', direction: 'forward', weight: 0.8 };
}

// GeneralSettingsSection.xaml.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/Settings/GeneralSettingsSection.xaml.cs';
  nodes.push(fileNode(p, 'Code-behind for GeneralSettingsSection; initializes the control with a view-model and delegates search-filter application to SettingsGroupFilter.', ['component', 'settings', 'code-behind'], 'simple'));
  const cn = classNode(p, 'GeneralSettingsSection', [7, 19], 'WinUI UserControl for the General settings section, wiring a view-model and implementing ISettingsSearchFilter via SettingsGroupFilter.', ['component', 'settings', 'event-handler'], 'simple');
  nodes.push(cn);
  edges.push(contains(p, cn.id));
  edges.push(exportEdge(p, cn.id));
}

// ISettingsSearchFilter.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/Settings/ISettingsSearchFilter.cs';
  nodes.push(fileNode(p, 'Defines ISettingsSearchFilter interface and SettingsGroupFilter helper that walks a UserControl tree to show/hide SettingsGroup elements by keyword tag matching.', ['type-definition', 'settings', 'utility'], 'moderate'));
  const iface = classNode(p, 'ISettingsSearchFilter', [9, 12], 'Interface requiring settings controls to implement ApplySearchFilter(groupKey).', ['type-definition', 'settings'], 'simple');
  const impl = classNode(p, 'SettingsGroupFilter', [14, 49], 'Stateful helper that saves original visibilities and toggles SettingsGroup visibility based on semicolon-separated tag matching for search.', ['utility', 'settings', 'component'], 'moderate');
  nodes.push(iface, impl);
  edges.push(contains(p, iface.id));
  edges.push(contains(p, impl.id));
  edges.push(exportEdge(p, iface.id));
  edges.push(exportEdge(p, impl.id));
}

// MemoryDiagnosticsCard.xaml
{
  const p = 'src/Wavee.UI.WinUI/Controls/Settings/MemoryDiagnosticsCard.xaml';
  nodes.push(fileNode(p, 'XAML layout for the MemoryDiagnosticsCard settings panel, presenting live GC heap statistics and memory counters.', ['component', 'settings', 'markup'], 'moderate'));
}

// MemoryDiagnosticsCard.xaml.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/Settings/MemoryDiagnosticsCard.xaml.cs';
  nodes.push(fileNode(p, 'Code-behind for MemoryDiagnosticsCard; resolves MemoryDiagnosticsService on load, creates and starts the view-model, and disposes it on unload.', ['component', 'settings', 'event-handler'], 'simple'));
  const cn = classNode(p, 'MemoryDiagnosticsCard', [9, 40], 'WinUI UserControl that lazily creates a MemoryDiagnosticsViewModel from DI on Loaded and disposes it on Unloaded.', ['component', 'settings', 'lifecycle'], 'moderate');
  nodes.push(cn);
  edges.push(contains(p, cn.id));
  edges.push(exportEdge(p, cn.id));
}

// PlaybackAudioSettingsSection.xaml
{
  const p = 'src/Wavee.UI.WinUI/Controls/Settings/PlaybackAudioSettingsSection.xaml';
  nodes.push(fileNode(p, 'Minimal XAML shell for PlaybackAudioSettingsSection acting as a host container for the playback and audio sub-sections.', ['component', 'settings', 'markup'], 'simple'));
}

// PlaybackAudioSettingsSection.xaml.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/Settings/PlaybackAudioSettingsSection.xaml.cs';
  nodes.push(fileNode(p, 'Code-behind that creates and hosts PlaybackSettingsSection and AudioSettingsSection sub-controls, delegating search-filter calls to both children.', ['component', 'settings', 'code-behind'], 'simple'));
  const cn = classNode(p, 'PlaybackAudioSettingsSection', [7, 30], 'Composite settings section holding playback and audio sub-sections; forwards ApplySearchFilter to both children.', ['component', 'settings', 'composite'], 'simple');
  nodes.push(cn);
  edges.push(contains(p, cn.id));
  edges.push(exportEdge(p, cn.id));
}

// PlaybackSettingsSection.xaml
{
  const p = 'src/Wavee.UI.WinUI/Controls/Settings/PlaybackSettingsSection.xaml';
  nodes.push(fileNode(p, 'XAML layout for the Playback settings section containing controls for crossfade, gapless playback, quality, and related preferences.', ['component', 'settings', 'markup'], 'moderate'));
}

// PlaybackSettingsSection.xaml.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/Settings/PlaybackSettingsSection.xaml.cs';
  nodes.push(fileNode(p, 'Code-behind for PlaybackSettingsSection; binds a view-model and delegates search-filter application to SettingsGroupFilter.', ['component', 'settings', 'code-behind'], 'simple'));
  const cn = classNode(p, 'PlaybackSettingsSection', [7, 19], 'UserControl for playback settings backed by a view-model; implements ISettingsSearchFilter.', ['component', 'settings', 'event-handler'], 'simple');
  nodes.push(cn);
  edges.push(contains(p, cn.id));
  edges.push(exportEdge(p, cn.id));
}

// SettingsGroupHeader.xaml
{
  const p = 'src/Wavee.UI.WinUI/Controls/Settings/SettingsGroupHeader.xaml';
  nodes.push(fileNode(p, 'XAML template for SettingsGroupHeader displaying a title, description, and icon glyph above a settings group.', ['component', 'settings', 'markup'], 'simple'));
}

// SettingsGroupHeader.xaml.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/Settings/SettingsGroupHeader.xaml.cs';
  nodes.push(fileNode(p, 'Code-behind for SettingsGroupHeader; exposes Title, Description, and IconGlyph dependency properties for XAML bindings.', ['component', 'settings', 'dependency-properties'], 'moderate'));
  const cn = classNode(p, 'SettingsGroupHeader', [6, 39], 'WinUI control with Title, Description, and IconGlyph DPs for rendering a decorative header above groups of settings rows.', ['component', 'settings', 'utility'], 'moderate');
  nodes.push(cn);
  edges.push(contains(p, cn.id));
  edges.push(exportEdge(p, cn.id));
}

// StorageNetworkSettingsSection.xaml
{
  const p = 'src/Wavee.UI.WinUI/Controls/Settings/StorageNetworkSettingsSection.xaml';
  nodes.push(fileNode(p, 'XAML layout for the Storage and Network settings section including local folder management, TMDB token entry, cache controls, and local-file rescan options.', ['component', 'settings', 'markup'], 'complex'));
}

// StorageNetworkSettingsSection.xaml.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/Settings/StorageNetworkSettingsSection.xaml.cs';
  nodes.push(fileNode(p, 'Code-behind for StorageNetworkSettingsSection; handles local folder add/remove, TMDB token verify/clear, enrichment trigger, cache clear, rescan, and TMDB status pill sync.', ['component', 'settings', 'event-handler'], 'complex'));
  const cn = classNode(p, 'StorageNetworkSettingsSection', [11, 134], 'Settings section managing local media folders, TMDB API token, metadata enrichment, and cache operations by delegating commands to the view-model.', ['component', 'settings', 'event-handler'], 'complex');
  nodes.push(cn);
  edges.push(contains(p, cn.id));
  edges.push(exportEdge(p, cn.id));
}

// SectionShelf.xaml
{
  const p = 'src/Wavee.UI.WinUI/Controls/Shelf/SectionShelf.xaml';
  nodes.push(fileNode(p, 'XAML template for SectionShelf defining the header row with title, subtitle, and a show-all button alongside the scrollable content area.', ['component', 'shelf', 'markup'], 'simple'));
}

// SectionShelf.xaml.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/Shelf/SectionShelf.xaml.cs';
  nodes.push(fileNode(p, 'Code-behind for SectionShelf; exposes a rich set of DPs (Title, Subtitle, ShowAllCommand, ItemsSource, ItemTemplate, layout dimensions) forwarded to the inner ShelfScroller.', ['component', 'shelf', 'dependency-properties'], 'complex', 'SectionShelf is a facade over ShelfScroller, re-exposing its DPs so callers need only a single control element.'));
  const cn = classNode(p, 'SectionShelf', [13, 118], 'High-level shelf container exposing title, subtitle, show-all and forwarding item source and layout DPs to the embedded ShelfScroller.', ['component', 'shelf', 'facade'], 'complex');
  nodes.push(cn);
  edges.push(contains(p, cn.id));
  edges.push(exportEdge(p, cn.id));
}

// ShelfLayout.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/Shelf/ShelfLayout.cs';
  nodes.push(fileNode(p, 'Custom WinUI VirtualizingLayout for horizontal shelves; computes item widths from available space and calculates the visible range for ItemsRepeater virtualization.', ['component', 'shelf', 'layout'], 'moderate'));
  const cn = classNode(p, 'ShelfLayout', [22, 146], 'VirtualizingLayout arranging items in a single horizontal row, resolving item width from configurable min/max and computing a viewport-clipped visible range for efficient virtualization.', ['component', 'shelf', 'layout'], 'complex');
  nodes.push(cn);
  edges.push(contains(p, cn.id));
  edges.push(exportEdge(p, cn.id));
}

// ShelfScroller.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/Shelf/ShelfScroller.cs';
  nodes.push(fileNode(p, 'Core shelf scrolling control wrapping ScrollView and ItemsRepeater with ShelfLayout; manages page-left/right commands, pointer-wheel pass-through, identity-keyed repeater recycling, and paging state updates.', ['component', 'shelf', 'scrolling'], 'complex', 'Uses DispatcherQueue.TryEnqueue for deferred paging state and identity-based repeater rebuild to prevent stale recycling when the source collection identity changes.'));
  const cn = classNode(p, 'ShelfScroller', [22, 492], 'Templated control hosting a ScrollView and ItemsRepeater; exposes paging commands, CanPageLeft/Right, HasOverflow, and handles identity-keyed source replacement to force clean item recycling.', ['component', 'shelf', 'scrolling'], 'complex');
  nodes.push(cn);
  edges.push(contains(p, cn.id));
  edges.push(exportEdge(p, cn.id));
}

// ShelfStyles.xaml
{
  const p = 'src/Wavee.UI.WinUI/Controls/Shelf/ShelfStyles.xaml';
  nodes.push(fileNode(p, 'XAML resource dictionary containing the default ControlTemplate and style for ShelfScroller including scroll chrome and navigation button overlays.', ['component', 'shelf', 'styles'], 'simple'));
}

// PodcastEpisodeRecommendationCard.xaml
{
  const p = 'src/Wavee.UI.WinUI/Controls/ShowEpisode/PodcastEpisodeRecommendationCard.xaml';
  nodes.push(fileNode(p, 'XAML layout for the podcast episode recommendation card, displaying cover art, title, duration, and a play button for a recommended episode.', ['component', 'podcast', 'markup'], 'moderate'));
}

// PodcastEpisodeRecommendationCard.xaml.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/ShowEpisode/PodcastEpisodeRecommendationCard.xaml.cs';
  nodes.push(fileNode(p, 'Code-behind for PodcastEpisodeRecommendationCard; reacts to Recommendation DP changes, loads cover art, handles hover/tap navigation with connected animation, and delegates play to NavigationHelpers.', ['component', 'podcast', 'event-handler'], 'complex'));
  const cn = classNode(p, 'PodcastEpisodeRecommendationCard', [13, 135], 'Card control for a recommended podcast episode with cover image loading, hover state, tap-to-navigate with connected animation, and play-button delegation.', ['component', 'podcast', 'card'], 'complex');
  nodes.push(cn);
  edges.push(contains(p, cn.id));
  edges.push(exportEdge(p, cn.id));
}

// ShowEpisodeRow.xaml
{
  const p = 'src/Wavee.UI.WinUI/Controls/ShowEpisode/ShowEpisodeRow.xaml';
  nodes.push(fileNode(p, 'XAML layout for the show episode list row, containing cover art, title, description snippet, playback progress bar, and action cluster with play, like, and more buttons.', ['component', 'podcast', 'markup'], 'complex'));
}

// ShowEpisodeRow.xaml.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/ShowEpisode/ShowEpisodeRow.xaml.cs';
  nodes.push(fileNode(p, 'Code-behind for ShowEpisodeRow; applies episode data with dynamic theme-aware color brushes derived from cover color, animates hover opacity via Storyboard, and raises OpenRequested/PlayRequested/LikeRequested events.', ['component', 'podcast', 'event-handler'], 'complex'));
  const cn = classNode(p, 'ShowEpisodeRow', [21, 378], 'Episode row control with dynamic cover-color theming, progress fill, hover opacity animation via Storyboard, and action events for open, play, and like.', ['component', 'podcast', 'event-handler'], 'complex');
  nodes.push(cn);
  edges.push(contains(p, cn.id));
  edges.push(exportEdge(p, cn.id));
}

// ShowResumeBanner.xaml
{
  const p = 'src/Wavee.UI.WinUI/Controls/ShowEpisode/ShowResumeBanner.xaml';
  nodes.push(fileNode(p, 'XAML layout for ShowResumeBanner, the hero-style banner at the top of a podcast show page prompting the user to resume the latest episode.', ['component', 'podcast', 'markup'], 'moderate'));
}

// ShowResumeBanner.xaml.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/ShowEpisode/ShowResumeBanner.xaml.cs';
  nodes.push(fileNode(p, 'Code-behind for ShowResumeBanner; applies episode metadata, derives a full dark palette from cover color using Darken/WithAlpha helpers, loads cover art, tracks progress fill, and animates hover scale via composition.', ['component', 'podcast', 'event-handler'], 'complex'));
  const cn = classNode(p, 'ShowResumeBanner', [23, 287], 'Hero banner control for resuming a podcast episode; derives a multi-stop color palette from cover art, animates hover scale using ElementCompositionPreview, and raises OpenRequested/PlayRequested.', ['component', 'podcast', 'hero-banner'], 'complex');
  nodes.push(cn);
  edges.push(contains(p, cn.id));
  edges.push(exportEdge(p, cn.id));
}

// ShowUpNextCard.xaml
{
  const p = 'src/Wavee.UI.WinUI/Controls/ShowEpisode/ShowUpNextCard.xaml';
  nodes.push(fileNode(p, 'XAML layout for ShowUpNextCard, a compact card presenting the next queued podcast episode with cover art, title, progress, and play button.', ['component', 'podcast', 'markup'], 'moderate'));
}

// ShowUpNextCard.xaml.cs
{
  const p = 'src/Wavee.UI.WinUI/Controls/ShowEpisode/ShowUpNextCard.xaml.cs';
  nodes.push(fileNode(p, 'Code-behind for ShowUpNextCard; computes accent brushes from cover color including luma-based foreground selection, applies episode state, handles hover scale animation via composition, and raises open/play events.', ['component', 'podcast', 'event-handler'], 'complex'));
  const cn = classNode(p, 'ShowUpNextCard', [20, 245], 'Up-next episode card with cover-color-derived accent palette, luma-aware foreground selection, composition hover scale, progress fill, and open/play event delegation.', ['component', 'podcast', 'card'], 'complex');
  nodes.push(cn);
  edges.push(contains(p, cn.id));
  edges.push(exportEdge(p, cn.id));
}

console.log('nodes:', nodes.length, 'edges:', edges.length);

const out = { nodes, edges };
fs.writeFileSync('C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-34.json', JSON.stringify(out, null, 2), { encoding: 'utf8' });
console.log('Written batch-34.json');
