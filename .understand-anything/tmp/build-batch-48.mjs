import fs from 'fs';

const nodes = [];
const edges = [];

// ===== XAML/Markup files =====

nodes.push({
  id: 'file:src/Wavee.UI.WinUI/Styles/TabbedCommandBarStyles.xaml',
  type: 'file', name: 'TabbedCommandBarStyles.xaml',
  filePath: 'src/Wavee.UI.WinUI/Styles/TabbedCommandBarStyles.xaml',
  summary: 'XAML resource dictionary providing custom Style definitions for the TabbedCommandBar control, adapting it to the Wavee Fluent theme.',
  tags: ['ui-style', 'component', 'xaml', 'fluent-design'],
  complexity: 'simple'
});

nodes.push({
  id: 'file:src/Wavee.UI.WinUI/Styles/TextBlockStyles.xaml',
  type: 'file', name: 'TextBlockStyles.xaml',
  filePath: 'src/Wavee.UI.WinUI/Styles/TextBlockStyles.xaml',
  summary: 'XAML resource dictionary defining TextBlock typography styles (heading, body, caption variants) used across the Wavee WinUI app.',
  tags: ['ui-style', 'typography', 'xaml', 'component'],
  complexity: 'simple'
});

nodes.push({
  id: 'file:src/Wavee.UI.WinUI/Styles/ThemeDictionaries.xaml',
  type: 'file', name: 'ThemeDictionaries.xaml',
  filePath: 'src/Wavee.UI.WinUI/Styles/ThemeDictionaries.xaml',
  summary: 'XAML resource dictionary containing Light/Dark/HighContrast theme brush overrides that power the Wavee app theming system.',
  tags: ['ui-style', 'theming', 'xaml', 'fluent-design'],
  complexity: 'moderate'
});

nodes.push({
  id: 'file:src/Wavee.UI.WinUI/Themes/AiBrandTheme.xaml',
  type: 'file', name: 'AiBrandTheme.xaml',
  filePath: 'src/Wavee.UI.WinUI/Themes/AiBrandTheme.xaml',
  summary: 'XAML resource dictionary defining the AI feature brand colours, gradients, and visual tokens used by on-device AI lyrics affordances (Phi Silica).',
  tags: ['ui-style', 'theming', 'ai-feature', 'xaml'],
  complexity: 'moderate'
});

nodes.push({
  id: 'file:src/Wavee.UI.WinUI/Themes/ContextMenu.xaml',
  type: 'file', name: 'ContextMenu.xaml',
  filePath: 'src/Wavee.UI.WinUI/Themes/ContextMenu.xaml',
  summary: 'XAML resource dictionary providing custom MenuFlyout and context-menu item styles used throughout the Wavee WinUI app.',
  tags: ['ui-style', 'context-menu', 'xaml', 'component'],
  complexity: 'moderate'
});

// ===== AiSettingsViewModel.cs =====

nodes.push({
  id: 'file:src/Wavee.UI.WinUI/ViewModels/AiSettingsViewModel.cs',
  type: 'file', name: 'AiSettingsViewModel.cs',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/AiSettingsViewModel.cs',
  summary: 'ViewModel for the on-device AI settings page, managing Phi Silica model preparation lifecycle, opt-in toggle states for AI lyrics/bio features, and cancellation.',
  tags: ['view-model', 'ai-feature', 'settings', 'service'],
  complexity: 'complex'
});

nodes.push({
  id: 'class:src/Wavee.UI.WinUI/ViewModels/AiSettingsViewModel.cs:AiSettingsViewModel',
  type: 'class', name: 'AiSettingsViewModel',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/AiSettingsViewModel.cs',
  lineRange: [30, 280],
  summary: 'Observable ViewModel for the AI Settings page; orchestrates Phi Silica model preparation, reports status, and forwards opt-in toggle changes to AppSettings.',
  tags: ['view-model', 'ai-feature', 'mvvm', 'settings'],
  complexity: 'complex'
});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/AiSettingsViewModel.cs', target:'class:src/Wavee.UI.WinUI/ViewModels/AiSettingsViewModel.cs:AiSettingsViewModel', type:'contains', direction:'forward', weight:1.0});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/AiSettingsViewModel.cs', target:'class:src/Wavee.UI.WinUI/ViewModels/AiSettingsViewModel.cs:AiSettingsViewModel', type:'exports', direction:'forward', weight:0.8});

nodes.push({
  id: 'function:src/Wavee.UI.WinUI/ViewModels/AiSettingsViewModel.cs:BeginModelPreparation',
  type: 'function', name: 'BeginModelPreparation',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/AiSettingsViewModel.cs',
  lineRange: [165, 200],
  summary: 'Starts the async Phi Silica model preparation sequence, setting status flags and launching RunPreparationAsync.',
  tags: ['ai-feature', 'async', 'initialization'],
  complexity: 'moderate'
});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/AiSettingsViewModel.cs', target:'function:src/Wavee.UI.WinUI/ViewModels/AiSettingsViewModel.cs:BeginModelPreparation', type:'contains', direction:'forward', weight:1.0});

nodes.push({
  id: 'function:src/Wavee.UI.WinUI/ViewModels/AiSettingsViewModel.cs:RunPreparationAsync',
  type: 'function', name: 'RunPreparationAsync',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/AiSettingsViewModel.cs',
  lineRange: [202, 249],
  summary: 'Executes the actual Phi Silica LanguageModel.CreateAsync call, handles LAF policy errors, and updates preparation progress state.',
  tags: ['ai-feature', 'async', 'error-handling'],
  complexity: 'complex'
});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/AiSettingsViewModel.cs', target:'function:src/Wavee.UI.WinUI/ViewModels/AiSettingsViewModel.cs:RunPreparationAsync', type:'contains', direction:'forward', weight:1.0});

// ===== AlbumsLibraryViewModel.cs =====

nodes.push({
  id: 'file:src/Wavee.UI.WinUI/ViewModels/AlbumsLibraryViewModel.cs',
  type: 'file', name: 'AlbumsLibraryViewModel.cs',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/AlbumsLibraryViewModel.cs',
  summary: 'Large ViewModel managing the Albums Library view: two-mode album list (Liked/Full), selection, filtering, sorting, breadcrumb navigation, track playback, and save/unsave operations.',
  tags: ['view-model', 'library', 'album', 'complex'],
  complexity: 'complex'
});

nodes.push({
  id: 'class:src/Wavee.UI.WinUI/ViewModels/AlbumsLibraryViewModel.cs:AlbumsLibraryViewModel',
  type: 'class', name: 'AlbumsLibraryViewModel',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/AlbumsLibraryViewModel.cs',
  lineRange: [36, 1207],
  summary: 'Primary ViewModel for the Albums Library page, managing saved and liked-album lists, filter/sort pipeline, detail expansion, and playback dispatch.',
  tags: ['view-model', 'library', 'album', 'mvvm'],
  complexity: 'complex'
});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/AlbumsLibraryViewModel.cs', target:'class:src/Wavee.UI.WinUI/ViewModels/AlbumsLibraryViewModel.cs:AlbumsLibraryViewModel', type:'contains', direction:'forward', weight:1.0});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/AlbumsLibraryViewModel.cs', target:'class:src/Wavee.UI.WinUI/ViewModels/AlbumsLibraryViewModel.cs:AlbumsLibraryViewModel', type:'exports', direction:'forward', weight:0.8});

const albumsLibFuncs = [
  {name:'LoadDataAsync', range:[310,350], summary:'Fetches saved and liked album collections from the library service and populates the view lists.', tags:['async','library','data-loading']},
  {name:'OnSaveStateChanged', range:[789,861], summary:'Handles library save-state change events, refreshing album presence flags and updating filtered lists in response to user heart/unheart actions.', tags:['event-handler','library','reactive']},
  {name:'OnSelectedAlbumChanged', range:[565,594], summary:'Reacts to album selection, triggers detail loading and breadcrumb update for the selected library album.', tags:['event-handler','selection','navigation']},
  {name:'PlayTrackAsync', range:[418,452], summary:'Resolves the playback context for a specific track within a library album and dispatches play to the orchestrator.', tags:['async','playback','track']},
  {name:'SortAlbums', range:[999,1028], summary:'Applies the current sort criterion (title, artist, date, last-played) to the saved-album observable collection.', tags:['sorting','collection','utility']},
];
albumsLibFuncs.forEach(f => {
  nodes.push({id:'function:src/Wavee.UI.WinUI/ViewModels/AlbumsLibraryViewModel.cs:'+f.name, type:'function', name:f.name, filePath:'src/Wavee.UI.WinUI/ViewModels/AlbumsLibraryViewModel.cs', lineRange:f.range, summary:f.summary, tags:f.tags, complexity:'moderate'});
  edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/AlbumsLibraryViewModel.cs', target:'function:src/Wavee.UI.WinUI/ViewModels/AlbumsLibraryViewModel.cs:'+f.name, type:'contains', direction:'forward', weight:1.0});
});

// ===== AlbumViewModel.cs =====

nodes.push({
  id: 'file:src/Wavee.UI.WinUI/ViewModels/AlbumViewModel.cs',
  type: 'file', name: 'AlbumViewModel.cs',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/AlbumViewModel.cs',
  summary: 'The primary ViewModel for the Album detail page: loads full album metadata, manages track list, save state, related content, merch, artist NPV, theming, and playback dispatch.',
  tags: ['view-model', 'album', 'complex', 'playback'],
  complexity: 'complex'
});

nodes.push({
  id: 'class:src/Wavee.UI.WinUI/ViewModels/AlbumViewModel.cs:AlbumViewModel',
  type: 'class', name: 'AlbumViewModel',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/AlbumViewModel.cs',
  lineRange: [49, 2058],
  summary: 'Full-page ViewModel for an album, orchestrating metadata fetch, disc layout, save/follow toggling, related-content sidebars, theming, and multi-track playback.',
  tags: ['view-model', 'album', 'mvvm', 'complex'],
  complexity: 'complex'
});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/AlbumViewModel.cs', target:'class:src/Wavee.UI.WinUI/ViewModels/AlbumViewModel.cs:AlbumViewModel', type:'contains', direction:'forward', weight:1.0});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/AlbumViewModel.cs', target:'class:src/Wavee.UI.WinUI/ViewModels/AlbumViewModel.cs:AlbumViewModel', type:'exports', direction:'forward', weight:0.8});

const albumVmFuncs = [
  {name:'Initialize', range:[773,882], summary:'Bootstraps the album page from a URI: decodes the album ID, triggers partial pre-fill from library cache, then fires full metadata load.', tags:['initialization','async','album'], complexity:'complex'},
  {name:'ApplyDetailAsync', range:[1407,1504], summary:'Merges a fully-loaded AlbumDetail payload into the ViewModel, building disc groups, track list, copyright blocks, and related-content signals.', tags:['async','data-model','album'], complexity:'complex'},
  {name:'ApplyTheme', range:[1235,1286], summary:'Extracts the dominant colour from the album art and applies it as a page-level accent brush for the hero header gradient.', tags:['theming','ui','color-extraction'], complexity:'moderate'},
  {name:'BuildQueueAndPlay', range:[1676,1709], summary:'Constructs the playback context from the current album track list and dispatches play to the orchestrator from the selected track position.', tags:['playback','queue','async'], complexity:'moderate'},
  {name:'ApplySecondaryAlbumSectionsAsync', range:[1518,1572], summary:'Fetches and populates secondary album content: similar albums, recommended playlists, artist context, and merch.', tags:['async','content-loading','album'], complexity:'moderate'},
  {name:'Dispose', range:[2039,2057], summary:'Tears down all subscriptions, cancellation tokens, and services held by the AlbumViewModel.', tags:['lifecycle','cleanup','disposable'], complexity:'simple'},
];
albumVmFuncs.forEach(f => {
  nodes.push({id:'function:src/Wavee.UI.WinUI/ViewModels/AlbumViewModel.cs:'+f.name, type:'function', name:f.name, filePath:'src/Wavee.UI.WinUI/ViewModels/AlbumViewModel.cs', lineRange:f.range, summary:f.summary, tags:f.tags, complexity:f.complexity});
  edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/AlbumViewModel.cs', target:'function:src/Wavee.UI.WinUI/ViewModels/AlbumViewModel.cs:'+f.name, type:'contains', direction:'forward', weight:1.0});
});

// ===== ArtistBioViewModel.cs =====

nodes.push({
  id: 'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistBioViewModel.cs',
  type: 'file', name: 'ArtistBioViewModel.cs',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/Artist/ArtistBioViewModel.cs',
  summary: 'ViewModel for the artist biography section, loading bio text and optionally generating an AI summary via Phi Silica on-device model.',
  tags: ['view-model', 'artist', 'ai-feature', 'biography'],
  complexity: 'moderate'
});

nodes.push({
  id: 'class:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistBioViewModel.cs:ArtistBioViewModel',
  type: 'class', name: 'ArtistBioViewModel',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/Artist/ArtistBioViewModel.cs',
  lineRange: [25, 193],
  summary: 'Manages artist biography display; fetches raw bio text and, when AI is enabled, summarises it with Phi Silica streaming tokens.',
  tags: ['view-model', 'artist', 'ai-feature', 'mvvm'],
  complexity: 'moderate'
});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistBioViewModel.cs', target:'class:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistBioViewModel.cs:ArtistBioViewModel', type:'contains', direction:'forward', weight:1.0});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistBioViewModel.cs', target:'class:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistBioViewModel.cs:ArtistBioViewModel', type:'exports', direction:'forward', weight:0.8});

nodes.push({
  id: 'function:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistBioViewModel.cs:LoadBioSummaryAsync',
  type: 'function', name: 'LoadBioSummaryAsync',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/Artist/ArtistBioViewModel.cs',
  lineRange: [140, 185],
  summary: 'Asynchronously generates an AI summary of the biography text using Phi Silica LanguageModel, streaming tokens into the UI.',
  tags: ['ai-feature', 'async', 'streaming'],
  complexity: 'complex'
});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistBioViewModel.cs', target:'function:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistBioViewModel.cs:LoadBioSummaryAsync', type:'contains', direction:'forward', weight:1.0});

// ===== ArtistDiscographyViewModel.cs =====

nodes.push({
  id: 'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistDiscographyViewModel.cs',
  type: 'file', name: 'ArtistDiscographyViewModel.cs',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/Artist/ArtistDiscographyViewModel.cs',
  summary: 'ViewModel driving the artist discography section: groups releases by type, fetches paginated release batches, prefetches palette colours, and handles the inline album expander.',
  tags: ['view-model', 'artist', 'discography', 'complex'],
  complexity: 'complex'
});

nodes.push({
  id: 'class:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistDiscographyViewModel.cs:ArtistDiscographyViewModel',
  type: 'class', name: 'ArtistDiscographyViewModel',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/Artist/ArtistDiscographyViewModel.cs',
  lineRange: [36, 745],
  summary: 'Controls discography grouping, progressive loading of album releases, colour prefetch, and expand/collapse of the inline album detail panel.',
  tags: ['view-model', 'discography', 'artist', 'mvvm'],
  complexity: 'complex'
});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistDiscographyViewModel.cs', target:'class:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistDiscographyViewModel.cs:ArtistDiscographyViewModel', type:'contains', direction:'forward', weight:1.0});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistDiscographyViewModel.cs', target:'class:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistDiscographyViewModel.cs:ArtistDiscographyViewModel', type:'exports', direction:'forward', weight:0.8});

const discFuncs = [
  {name:'FetchRemainingDiscographyAsync', range:[507,603], summary:'Progressively fetches remaining paginated discography groups from SpClient and appends them to the per-type release lists.', tags:['async','data-loading','pagination']},
  {name:'PrefetchReleaseColorsAsync', range:[402,468], summary:'Batches CompositionImage colour extraction for loaded releases so album tiles can show dominant-colour backgrounds before full art loads.', tags:['async','colour-extraction','performance']},
  {name:'ExpandAlbum', range:[644,698], summary:'Triggers the inline album expander panel for the selected release, fetching its full track list and switching the grid layout to the expanded state.', tags:['ui','album','expansion']},
  {name:'ApplyOverview', range:[211,262], summary:'Initialises the discography from the artist overview payload, categorising releases into Albums, Singles, Compilations and dispatching them to type-keyed lists.', tags:['initialization','data-model','artist']},
];
discFuncs.forEach(f => {
  nodes.push({id:'function:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistDiscographyViewModel.cs:'+f.name, type:'function', name:f.name, filePath:'src/Wavee.UI.WinUI/ViewModels/Artist/ArtistDiscographyViewModel.cs', lineRange:f.range, summary:f.summary, tags:f.tags, complexity:'complex'});
  edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistDiscographyViewModel.cs', target:'function:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistDiscographyViewModel.cs:'+f.name, type:'contains', direction:'forward', weight:1.0});
});

// ===== ArtistExtrasViewModel.cs =====

nodes.push({
  id: 'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistExtrasViewModel.cs',
  type: 'file', name: 'ArtistExtrasViewModel.cs',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/Artist/ArtistExtrasViewModel.cs',
  summary: 'ViewModel for the artist extras section: concerts near the user, merchandise, and music videos parsed from the artist overview.',
  tags: ['view-model', 'artist', 'concerts', 'extras'],
  complexity: 'moderate'
});

nodes.push({
  id: 'class:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistExtrasViewModel.cs:ArtistExtrasViewModel',
  type: 'class', name: 'ArtistExtrasViewModel',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/Artist/ArtistExtrasViewModel.cs',
  lineRange: [27, 212],
  summary: 'Provides concerts, merch, and music video collections for the artist page extras tab, populated from the artist overview payload.',
  tags: ['view-model', 'artist', 'concerts', 'mvvm'],
  complexity: 'moderate'
});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistExtrasViewModel.cs', target:'class:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistExtrasViewModel.cs:ArtistExtrasViewModel', type:'contains', direction:'forward', weight:1.0});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistExtrasViewModel.cs', target:'class:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistExtrasViewModel.cs:ArtistExtrasViewModel', type:'exports', direction:'forward', weight:0.8});

nodes.push({
  id: 'function:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistExtrasViewModel.cs:ApplyOverview',
  type: 'function', name: 'ApplyOverview',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/Artist/ArtistExtrasViewModel.cs',
  lineRange: [84, 178],
  summary: 'Parses concerts, merch items, and music videos from the artist overview DTO and populates the observable collections.',
  tags: ['data-model', 'initialization', 'artist'],
  complexity: 'moderate'
});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistExtrasViewModel.cs', target:'function:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistExtrasViewModel.cs:ApplyOverview', type:'contains', direction:'forward', weight:1.0});

// ===== ArtistHeaderViewModel.cs =====

nodes.push({
  id: 'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistHeaderViewModel.cs',
  type: 'file', name: 'ArtistHeaderViewModel.cs',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/Artist/ArtistHeaderViewModel.cs',
  summary: 'ViewModel for the artist page hero header: exposes artist name, image, listener count, follow state, tour banner, and theme accent colour.',
  tags: ['view-model', 'artist', 'header', 'theming'],
  complexity: 'complex'
});

nodes.push({
  id: 'class:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistHeaderViewModel.cs:ArtistHeaderViewModel',
  type: 'class', name: 'ArtistHeaderViewModel',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/Artist/ArtistHeaderViewModel.cs',
  lineRange: [27, 341],
  summary: 'Drives the artist hero header UI including follow/unfollow toggle, tour-banner text composition, and page-level theme extraction.',
  tags: ['view-model', 'artist', 'header', 'mvvm'],
  complexity: 'complex'
});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistHeaderViewModel.cs', target:'class:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistHeaderViewModel.cs:ArtistHeaderViewModel', type:'contains', direction:'forward', weight:1.0});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistHeaderViewModel.cs', target:'class:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistHeaderViewModel.cs:ArtistHeaderViewModel', type:'exports', direction:'forward', weight:0.8});

nodes.push({
  id: 'function:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistHeaderViewModel.cs:ApplyTheme',
  type: 'function', name: 'ApplyTheme',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/Artist/ArtistHeaderViewModel.cs',
  lineRange: [324, 334],
  summary: 'Applies the extracted dominant colour from the artist image as the page-level accent brush.',
  tags: ['theming', 'ui', 'color-extraction'],
  complexity: 'simple'
});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistHeaderViewModel.cs', target:'function:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistHeaderViewModel.cs:ApplyTheme', type:'contains', direction:'forward', weight:1.0});

nodes.push({
  id: 'function:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistHeaderViewModel.cs:TourBannerText',
  type: 'function', name: 'TourBannerText',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/Artist/ArtistHeaderViewModel.cs',
  lineRange: [240, 264],
  summary: 'Computes the localised tour-banner string (e.g. "X concerts near you") shown below the artist hero image.',
  tags: ['ui', 'concerts', 'localisation'],
  complexity: 'moderate'
});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistHeaderViewModel.cs', target:'function:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistHeaderViewModel.cs:TourBannerText', type:'contains', direction:'forward', weight:1.0});

// ===== ArtistPlaylistVm.cs =====
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistPlaylistVm.cs',
  type: 'file', name: 'ArtistPlaylistVm.cs',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/Artist/ArtistPlaylistVm.cs',
  summary: 'Minimal data class representing a playlist linked to an artist (e.g. Artist Radio or featured playlist) on the artist page.',
  tags: ['data-model', 'artist', 'playlist'],
  complexity: 'simple'
});

// ===== ArtistRelatedArtistsViewModel.cs =====
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistRelatedArtistsViewModel.cs',
  type: 'file', name: 'ArtistRelatedArtistsViewModel.cs',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/Artist/ArtistRelatedArtistsViewModel.cs',
  summary: 'ViewModel for the related-artists section on the artist page, populated from the artist overview payload.',
  tags: ['view-model', 'artist', 'related-artists'],
  complexity: 'simple'
});

nodes.push({
  id: 'class:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistRelatedArtistsViewModel.cs:ArtistRelatedArtistsViewModel',
  type: 'class', name: 'ArtistRelatedArtistsViewModel',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/Artist/ArtistRelatedArtistsViewModel.cs',
  lineRange: [19, 51],
  summary: 'Holds and exposes the list of related artists derived from the artist overview, resetting on navigation to a new artist.',
  tags: ['view-model', 'artist', 'mvvm'],
  complexity: 'simple'
});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistRelatedArtistsViewModel.cs', target:'class:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistRelatedArtistsViewModel.cs:ArtistRelatedArtistsViewModel', type:'contains', direction:'forward', weight:1.0});

// ===== ArtistReleaseVm.cs =====
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistReleaseVm.cs',
  type: 'file', name: 'ArtistReleaseVm.cs',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/Artist/ArtistReleaseVm.cs',
  summary: 'Data ViewModel representing a single release entry (album/single/compilation) in the artist discography list with palette colour and save state.',
  tags: ['data-model', 'artist', 'discography', 'album'],
  complexity: 'simple'
});

// ===== ArtistSocialLinkVm.cs =====
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistSocialLinkVm.cs',
  type: 'file', name: 'ArtistSocialLinkVm.cs',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/Artist/ArtistSocialLinkVm.cs',
  summary: 'Simple data record holding a social-media link (platform name and URL) for an artist profile.',
  tags: ['data-model', 'artist', 'social'],
  complexity: 'simple'
});

// ===== ArtistTopCityVm.cs =====
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistTopCityVm.cs',
  type: 'file', name: 'ArtistTopCityVm.cs',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/Artist/ArtistTopCityVm.cs',
  summary: 'Data record holding a top-city entry (city name, country, listener count) shown in the artist header.',
  tags: ['data-model', 'artist', 'analytics'],
  complexity: 'simple'
});

// ===== ArtistTopTracksViewModel.cs =====
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistTopTracksViewModel.cs',
  type: 'file', name: 'ArtistTopTracksViewModel.cs',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/Artist/ArtistTopTracksViewModel.cs',
  summary: 'ViewModel for the artist top-tracks section: paginates the top-10 list into a responsive multi-column grid, handles track play dispatch, selection, and lazy image enrichment for missing artwork.',
  tags: ['view-model', 'artist', 'top-tracks', 'playback'],
  complexity: 'complex'
});

nodes.push({
  id: 'class:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistTopTracksViewModel.cs:ArtistTopTracksViewModel',
  type: 'class', name: 'ArtistTopTracksViewModel',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/Artist/ArtistTopTracksViewModel.cs',
  lineRange: [33, 677],
  summary: 'Manages the top-tracks subset of an artist page: pagination across a column-aware grid, playback dispatch with pending-play feedback, and lazy image enrichment.',
  tags: ['view-model', 'artist', 'playback', 'mvvm'],
  complexity: 'complex'
});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistTopTracksViewModel.cs', target:'class:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistTopTracksViewModel.cs:ArtistTopTracksViewModel', type:'contains', direction:'forward', weight:1.0});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistTopTracksViewModel.cs', target:'class:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistTopTracksViewModel.cs:ArtistTopTracksViewModel', type:'exports', direction:'forward', weight:0.8});

const topTrackFuncs = [
  {name:'PlayTrackAsync', range:[385,483], summary:'Resolves the full play context for a selected top track (artist context or single-track fallback) and dispatches it to the playback orchestrator.', tags:['async','playback','track']},
  {name:'EnrichMissingTopTrackImagesAsync', range:[495,572], summary:'Fetches album art for top-track entries that arrived without an image URL, updating them lazily via SpClient.', tags:['async','image','enrichment']},
  {name:'LoadExtendedTopTracksAsync', range:[580,657], summary:'Fetches the full paginated top-tracks list beyond the initial 10, merging results into the observable slice.', tags:['async','data-loading','pagination']},
];
topTrackFuncs.forEach(f => {
  nodes.push({id:'function:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistTopTracksViewModel.cs:'+f.name, type:'function', name:f.name, filePath:'src/Wavee.UI.WinUI/ViewModels/Artist/ArtistTopTracksViewModel.cs', lineRange:f.range, summary:f.summary, tags:f.tags, complexity:'complex'});
  edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistTopTracksViewModel.cs', target:'function:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistTopTracksViewModel.cs:'+f.name, type:'contains', direction:'forward', weight:1.0});
});

// ===== ArtistTopTrackVm.cs =====
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistTopTrackVm.cs',
  type: 'file', name: 'ArtistTopTrackVm.cs',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/Artist/ArtistTopTrackVm.cs',
  summary: 'Observable data ViewModel for a single top-track row: track URI, name, play count, image, save state, and playback indicator flags.',
  tags: ['data-model', 'artist', 'track', 'observable'],
  complexity: 'moderate'
});

nodes.push({
  id: 'class:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistTopTrackVm.cs:ArtistTopTrackVm',
  type: 'class', name: 'ArtistTopTrackVm',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/Artist/ArtistTopTrackVm.cs',
  lineRange: [10, 99],
  summary: 'Row model for a top-track entry; implements INotifyPropertyChanged for property mutation during lazy enrichment.',
  tags: ['data-model', 'mvvm', 'observable', 'artist'],
  complexity: 'moderate'
});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistTopTrackVm.cs', target:'class:src/Wavee.UI.WinUI/ViewModels/Artist/ArtistTopTrackVm.cs:ArtistTopTrackVm', type:'contains', direction:'forward', weight:1.0});

// ===== ConcertVm.cs =====
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/ViewModels/Artist/ConcertVm.cs',
  type: 'file', name: 'ConcertVm.cs',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/Artist/ConcertVm.cs',
  summary: 'Data record representing a single concert event shown in the artist extras section (venue, date, location, ticket URL).',
  tags: ['data-model', 'artist', 'concerts'],
  complexity: 'simple'
});

// ===== MerchItemVm.cs =====
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/ViewModels/Artist/MerchItemVm.cs',
  type: 'file', name: 'MerchItemVm.cs',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/Artist/MerchItemVm.cs',
  summary: 'Data record for a merchandise item on the artist page (name, image, price, buy URL).',
  tags: ['data-model', 'artist', 'merchandise'],
  complexity: 'simple'
});

// ===== MusicVideoVm.cs =====
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/ViewModels/Artist/MusicVideoVm.cs',
  type: 'file', name: 'MusicVideoVm.cs',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/Artist/MusicVideoVm.cs',
  summary: 'Data record for a music video linked to an artist, exposing title, thumbnail, and Spotify URI for playback.',
  tags: ['data-model', 'artist', 'music-video'],
  complexity: 'simple'
});

// ===== RelatedArtistVm.cs =====
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/ViewModels/Artist/RelatedArtistVm.cs',
  type: 'file', name: 'RelatedArtistVm.cs',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/Artist/RelatedArtistVm.cs',
  summary: 'Minimal data record for a related-artist entry (URI, name, image) shown in the related-artists grid on the artist page.',
  tags: ['data-model', 'artist', 'related-artists'],
  complexity: 'simple'
});

// ===== ArtistAlbumGroupViewModel.cs =====
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/ViewModels/ArtistAlbumGroupViewModel.cs',
  type: 'file', name: 'ArtistAlbumGroupViewModel.cs',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/ArtistAlbumGroupViewModel.cs',
  summary: 'ViewModel managing a grouped album section within the artist library view: selection, list/grid view-mode toggle, lazy track loading, and play dispatch.',
  tags: ['view-model', 'artist', 'album', 'library'],
  complexity: 'moderate'
});

nodes.push({
  id: 'class:src/Wavee.UI.WinUI/ViewModels/ArtistAlbumGroupViewModel.cs:ArtistAlbumGroupViewModel',
  type: 'class', name: 'ArtistAlbumGroupViewModel',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/ArtistAlbumGroupViewModel.cs',
  lineRange: [18, 146],
  summary: 'Groups artist albums for the Artists Library tab; handles view-mode toggling, lazy track loading, album navigation, and playback.',
  tags: ['view-model', 'album', 'library', 'mvvm'],
  complexity: 'moderate'
});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/ArtistAlbumGroupViewModel.cs', target:'class:src/Wavee.UI.WinUI/ViewModels/ArtistAlbumGroupViewModel.cs:ArtistAlbumGroupViewModel', type:'contains', direction:'forward', weight:1.0});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/ArtistAlbumGroupViewModel.cs', target:'class:src/Wavee.UI.WinUI/ViewModels/ArtistAlbumGroupViewModel.cs:ArtistAlbumGroupViewModel', type:'exports', direction:'forward', weight:0.8});

nodes.push({
  id: 'function:src/Wavee.UI.WinUI/ViewModels/ArtistAlbumGroupViewModel.cs:PlayTrackAsync',
  type: 'function', name: 'PlayTrackAsync',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/ArtistAlbumGroupViewModel.cs',
  lineRange: [136, 145],
  summary: 'Dispatches playback for a specific track within a library album group, routing through the parent artist album item.',
  tags: ['async', 'playback', 'track'],
  complexity: 'simple'
});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/ArtistAlbumGroupViewModel.cs', target:'function:src/Wavee.UI.WinUI/ViewModels/ArtistAlbumGroupViewModel.cs:PlayTrackAsync', type:'contains', direction:'forward', weight:1.0});

// ===== ArtistAlbumItemViewModel.cs =====
nodes.push({
  id: 'file:src/Wavee.UI.WinUI/ViewModels/ArtistAlbumItemViewModel.cs',
  type: 'file', name: 'ArtistAlbumItemViewModel.cs',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/ArtistAlbumItemViewModel.cs',
  summary: 'ViewModel for a single album tile within the ArtistAlbumGroup: manages selection, card click/play events, and lazy track list loading for the list-mode expand.',
  tags: ['view-model', 'artist', 'album', 'library'],
  complexity: 'moderate'
});

nodes.push({
  id: 'class:src/Wavee.UI.WinUI/ViewModels/ArtistAlbumItemViewModel.cs:ArtistAlbumItemViewModel',
  type: 'class', name: 'ArtistAlbumItemViewModel',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/ArtistAlbumItemViewModel.cs',
  lineRange: [15, 85],
  summary: 'Row/tile model for one album in the artist library group; triggers navigation, play, and lazy track fetch on interaction.',
  tags: ['view-model', 'album', 'library', 'mvvm'],
  complexity: 'moderate'
});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/ArtistAlbumItemViewModel.cs', target:'class:src/Wavee.UI.WinUI/ViewModels/ArtistAlbumItemViewModel.cs:ArtistAlbumItemViewModel', type:'contains', direction:'forward', weight:1.0});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/ArtistAlbumItemViewModel.cs', target:'class:src/Wavee.UI.WinUI/ViewModels/ArtistAlbumItemViewModel.cs:ArtistAlbumItemViewModel', type:'exports', direction:'forward', weight:0.8});

nodes.push({
  id: 'function:src/Wavee.UI.WinUI/ViewModels/ArtistAlbumItemViewModel.cs:LoadTracksAsync',
  type: 'function', name: 'LoadTracksAsync',
  filePath: 'src/Wavee.UI.WinUI/ViewModels/ArtistAlbumItemViewModel.cs',
  lineRange: [64, 84],
  summary: 'Lazily loads track list for this album item when the user expands it to list mode, fetching from the library or SpClient.',
  tags: ['async', 'data-loading', 'track'],
  complexity: 'moderate'
});
edges.push({source:'file:src/Wavee.UI.WinUI/ViewModels/ArtistAlbumItemViewModel.cs', target:'function:src/Wavee.UI.WinUI/ViewModels/ArtistAlbumItemViewModel.cs:LoadTracksAsync', type:'contains', direction:'forward', weight:1.0});

// Write output
const output = {nodes, edges};
console.log('nodes:', nodes.length, 'edges:', edges.length);
fs.writeFileSync('C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-48.json', JSON.stringify(output, null, 2), {encoding:'utf8'});
console.log('Written batch-48.json');
