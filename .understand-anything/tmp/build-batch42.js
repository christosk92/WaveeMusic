const fs = require('fs');

const nodes = [];
const edges = [];

function addFile(path, summary, tags, complexity, languageNotes) {
  const name = path.split('/').pop();
  const node = {
    id: 'file:' + path,
    type: 'file',
    name,
    filePath: path,
    summary,
    tags,
    complexity
  };
  if (languageNotes) node.languageNotes = languageNotes;
  nodes.push(node);
}

function addClass(path, name, lineRange, summary, tags, complexity) {
  nodes.push({
    id: 'class:' + path + ':' + name,
    type: 'class',
    name,
    filePath: path,
    lineRange,
    summary,
    tags,
    complexity
  });
  edges.push({source: 'file:' + path, target: 'class:' + path + ':' + name, type: 'contains', direction: 'forward', weight: 1.0});
}

function addFunction(path, name, lineRange, summary, tags, complexity) {
  nodes.push({
    id: 'function:' + path + ':' + name,
    type: 'function',
    name,
    filePath: path,
    lineRange,
    summary,
    tags,
    complexity
  });
  edges.push({source: 'file:' + path, target: 'function:' + path + ':' + name, type: 'contains', direction: 'forward', weight: 1.0});
}

// 1. RetryHandler.cs
const p1 = 'src/Wavee.UI.WinUI/Helpers/Application/RetryHandler.cs';
addFile(p1, 'HTTP DelegatingHandler that retries transient failures with exponential back-off, used in the WinUI app HTTP client pipeline.', ['middleware', 'http', 'retry', 'utility'], 'moderate');
addClass(p1, 'RetryHandler', [1, 55], 'DelegatingHandler subclass implementing HTTP retry logic with configurable attempts and back-off.', ['middleware', 'http', 'retry'], 'moderate');
addFunction(p1, 'SendAsync', [17, 43], 'Overrides SendAsync to retry transient HTTP errors with delay between attempts.', ['middleware', 'http', 'retry'], 'moderate');
addFunction(p1, 'CloneRequest', [45, 54], 'Clones an HttpRequestMessage so it can be re-sent after a failed attempt.', ['utility', 'http'], 'simple');

// 2. TitleBarHelper.cs
const p2 = 'src/Wavee.UI.WinUI/Helpers/Application/TitleBarHelper.cs';
addFile(p2, 'Utility for theming the WinUI title-bar caption buttons to match the app accent or dark/light mode.', ['utility', 'ui', 'title-bar', 'theming'], 'simple');
addClass(p2, 'TitleBarHelper', [1, 45], 'Static helper that applies transparent or themed colors to the AppWindow title-bar caption buttons.', ['utility', 'ui', 'theming'], 'simple');

// 3. ConnectedAnimationHelper.cs
const p3 = 'src/Wavee.UI.WinUI/Helpers/ConnectedAnimationHelper.cs';
addFile(p3, 'Manages WinUI ConnectedAnimation lifecycle: prepare, start, coordinate, and cancel for hero card transitions across navigation.', ['animation', 'navigation', 'connected-animation', 'utility'], 'complex', 'ConnectedAnimationService is stateful per-frame; helper caches pending animation keys to safely start or cancel.');
addClass(p3, 'ConnectedAnimationHelper', [1, 190], 'Orchestrates connected animations with support for coordinated element groups and back-stack transitions.', ['animation', 'navigation', 'connected-animation'], 'complex');
addFunction(p3, 'PrepareAnimation', [55, 68], 'Prepares a forward connected animation and registers the key for deferred start.', ['animation', 'navigation'], 'simple');
addFunction(p3, 'TryStartAnimation', [82, 106], 'Attempts to start a prepared connected animation on the target element, with fallback fade.', ['animation', 'navigation'], 'moderate');
addFunction(p3, 'TryStartAnimationWithCoordinatedElements', [116, 139], 'Starts a connected animation with additional coordinated UI elements moving in sync.', ['animation', 'navigation'], 'moderate');
addFunction(p3, 'CancelPending', [145, 159], 'Cancels any pending prepared animations to prevent stale transitions.', ['animation', 'navigation'], 'simple');
addFunction(p3, 'PrepareBackAnimation', [167, 179], 'Prepares a back-navigation connected animation from the destination element.', ['animation', 'navigation'], 'simple');

// 4. ContentPageController.cs
const p4 = 'src/Wavee.UI.WinUI/Helpers/ContentPageController.cs';
addFile(p4, 'Coordinates the shimmer-to-content crossfade lifecycle for content pages: schedules a delayed crossfade and manages the visibility transition when data loads.', ['animation', 'ui', 'loading', 'component'], 'complex');
addClass(p4, 'ContentPageController', [1, 187], 'Controls shimmer placeholder visibility and content crossfade timing during page loading.', ['animation', 'ui', 'loading', 'component'], 'complex');
addFunction(p4, 'ContentPageController_ctor', [38, 42], 'Constructor that wires the IsLoading dependency property change callback.', ['component', 'initialization'], 'simple');
addFunction(p4, 'OnIsLoadingChanged', [60, 75], 'Reacts to IsLoading changes to either show shimmer or schedule a content crossfade.', ['event-handler', 'loading'], 'simple');
addFunction(p4, 'ScheduleCrossfade', [82, 104], 'Schedules a delayed crossfade from shimmer to content using DispatcherTimer.', ['animation', 'loading'], 'moderate');
addFunction(p4, 'CrossfadeToContentAsync', [155, 186], 'Performs the actual opacity animation to transition from shimmer placeholder to real content.', ['animation', 'ui'], 'moderate');

// 5. Debouncer.cs
const p5 = 'src/Wavee.UI.WinUI/Helpers/Debouncer.cs';
addFile(p5, 'Async debounce utility that cancels a pending async operation when a new one arrives within the debounce window.', ['utility', 'async', 'debounce'], 'moderate');
addClass(p5, 'Debouncer', [1, 67], 'Thread-safe debouncer that cancels the previous CancellationTokenSource and fires a new async action after a configurable delay.', ['utility', 'async', 'debounce'], 'moderate');
addFunction(p5, 'DebounceAsync', [32, 49], 'Cancels any in-flight operation and schedules the callback after the debounce delay.', ['utility', 'async', 'debounce'], 'moderate');

// 6. ErrorMapper.cs
const p6 = 'src/Wavee.UI.WinUI/Helpers/ErrorMapper.cs';
addFile(p6, 'Maps exception types to human-readable error messages for display in the UI and playback error dialogs.', ['utility', 'error-handling', 'ui'], 'simple');
addClass(p6, 'ErrorMapper', [1, 44], 'Static mapper translating exceptions to localized user-facing error strings.', ['utility', 'error-handling'], 'simple');

// 7. HeroCardAnimations.cs
const p7 = 'src/Wavee.UI.WinUI/Helpers/HeroCardAnimations.cs';
addFile(p7, 'Attached behavior providing scale/opacity selection animation for hero cards when IsSelected changes.', ['animation', 'attached-behavior', 'ui', 'component'], 'moderate');
addClass(p7, 'HeroCardAnimations', [1, 52], 'Attached property class that plays a scale-up/opacity animation on hero card selection.', ['animation', 'attached-behavior', 'component'], 'moderate');
addFunction(p7, 'OnIsSelectedChanged', [28, 51], 'Runs the scale and opacity composition animation when the IsSelected attached property changes.', ['animation', 'event-handler'], 'moderate');

// 8. IContentPageHost.cs
const p8 = 'src/Wavee.UI.WinUI/Helpers/IContentPageHost.cs';
addFile(p8, 'Interface defining the content-page host contract: provides references to the shimmer layer, content container, header panel, and crossfade controller.', ['type-definition', 'component', 'interface'], 'moderate');
addClass(p8, 'IContentPageHost', [1, 52], 'Interface that page controls implement to expose shimmer/content elements to ContentPageController.', ['type-definition', 'interface', 'component'], 'moderate');

// 9. LocalImagePaletteHelper.cs
const p9 = 'src/Wavee.UI.WinUI/Helpers/LocalImagePaletteHelper.cs';
addFile(p9, 'Extracts the dominant color from a local image file by sampling pixels via a SoftwareBitmap decode, used to seed the page tint color.', ['utility', 'image-processing', 'color', 'ui'], 'moderate');
addClass(p9, 'LocalImagePaletteHelper', [1, 52], 'Async helper that decodes a local image and computes the dominant hex color string.', ['utility', 'image-processing', 'color'], 'moderate');
addFunction(p9, 'TryExtractDominantHexAsync', [22, 51], 'Decodes a local image file and returns the dominant color as a hex string by sampling pixel data.', ['utility', 'image-processing', 'color'], 'moderate');

// 10. LyricsContentParser.cs
const p10 = 'src/Wavee.UI.WinUI/Helpers/Lyrics/LyricsContentParser.cs';
addFile(p10, 'Parses multiple lyrics formats (QRC/KRC, LRC, TTML) into a unified LyricsData model with timed lines and optional word-level highlights.', ['utility', 'lyrics', 'parsing', 'serialization'], 'complex');
addClass(p10, 'LyricsContentParser', [1, 358], 'Multi-format lyrics parser supporting QRC/KRC, LRC timestamp, and TTML with word-level segmentation.', ['utility', 'lyrics', 'parsing'], 'complex');
addFunction(p10, 'Parse', [25, 47], 'Entry point that detects the lyrics format and dispatches to the appropriate sub-parser.', ['utility', 'lyrics', 'parsing'], 'simple');
addFunction(p10, 'DetectFormat', [51, 68], 'Sniffs the raw lyrics string to identify whether it is QRC/KRC, LRC, or TTML.', ['utility', 'lyrics'], 'simple');
addFunction(p10, 'ParseQrcKrc', [72, 110], 'Parses QRC/KRC format lyrics with millisecond timestamps and word-level segments.', ['lyrics', 'parsing'], 'moderate');
addFunction(p10, 'ParseLrc', [117, 189], 'Parses LRC format with line-level timestamps and optional enhanced word timing.', ['lyrics', 'parsing'], 'moderate');
addFunction(p10, 'ParseTtml', [193, 223], 'Parses TTML XML lyrics document extracting timed spans into lyric lines.', ['lyrics', 'parsing', 'xml'], 'moderate');
addFunction(p10, 'ParseTtmlSegment', [225, 277], 'Recursively parses a TTML span element including nested word-level timed segments.', ['lyrics', 'parsing', 'xml'], 'moderate');
addFunction(p10, 'ParseTtmlTime', [279, 319], 'Converts TTML time attribute strings (HH:MM:SS.mmm or tick notation) to milliseconds.', ['utility', 'lyrics', 'parsing'], 'moderate');
addFunction(p10, 'EnsureEndMs', [323, 357], 'Fills in missing end-time values for lyric lines by inferring from the following line start.', ['utility', 'lyrics'], 'simple');

// 11. TranscriptToLyricsMapper.cs
const p11 = 'src/Wavee.UI.WinUI/Helpers/Lyrics/TranscriptToLyricsMapper.cs';
addFile(p11, 'Maps a Spotify transcript response (episode captions) to the shared LyricsData model used by the lyrics rendering control.', ['utility', 'lyrics', 'mapping', 'serialization'], 'moderate');
addClass(p11, 'TranscriptToLyricsMapper', [1, 112], 'Converts transcript DTOs with highlight ranges into timed lyric lines compatible with the lyrics canvas.', ['utility', 'lyrics', 'mapping'], 'moderate');
addFunction(p11, 'ToLyricsData', [24, 65], 'Transforms a list of transcript segments into LyricsData with timed lines.', ['utility', 'lyrics', 'mapping'], 'moderate');
addFunction(p11, 'MapHighlights', [75, 111], 'Converts transcript highlight spans into word-level LyricsWord segments within a lyric line.', ['utility', 'lyrics', 'mapping'], 'moderate');

// 12. AlbumNavigationHelper.cs
const p12 = 'src/Wavee.UI.WinUI/Helpers/Navigation/AlbumNavigationHelper.cs';
addFile(p12, 'Resolves an album or track URI into a navigation parameter and triggers navigation to the album detail page.', ['navigation', 'utility', 'ui'], 'moderate');
addClass(p12, 'AlbumNavigationHelper', [1, 71], 'Static helper that resolves album/track URIs and navigates to the album page with connected animation.', ['navigation', 'utility'], 'moderate');
addFunction(p12, 'NavigateToAlbum', [43, 70], 'Resolves a URI to an album or disc offset and calls NavigationHelpers.OpenAlbum with animation.', ['navigation', 'utility'], 'moderate');

// 13. CreatePlaylistParameter.cs
const p13 = 'src/Wavee.UI.WinUI/Helpers/Navigation/CreatePlaylistParameter.cs';
addFile(p13, 'Simple navigation parameter record carrying the initial track list for the create-playlist flow.', ['data-model', 'navigation', 'type-definition'], 'simple');

// 14. NavigationHelpers.cs
const p14 = 'src/Wavee.UI.WinUI/Helpers/Navigation/NavigationHelpers.cs';
addFile(p14, 'Central navigation facade providing typed OpenX methods for every page in the app, tab management, icon creation, and frame navigation with ConnectedAnimation support.', ['navigation', 'utility', 'singleton', 'entry-point'], 'complex');
addClass(p14, 'NavigationHelpers', [1, 791], 'Static helper exposing all app navigation entry points, tab creation, and frame routing for the WinUI shell.', ['navigation', 'utility', 'singleton'], 'complex');
addFunction(p14, 'OpenArtistDiscography', [76, 90], 'Navigates to the artist discography page with connected animation preparation.', ['navigation'], 'simple');
addFunction(p14, 'OpenEpisodePage', [356, 380], 'Navigates to a podcast episode detail page with metadata parameter.', ['navigation'], 'simple');
addFunction(p14, 'PlayEpisode', [387, 425], 'Resolves and starts podcast episode playback, navigating to the episode page on success.', ['navigation', 'playback'], 'moderate');
addFunction(p14, 'OpenCreatePlaylist', [441, 460], 'Opens the create-playlist dialog or page with optional pre-populated track list.', ['navigation'], 'moderate');
addFunction(p14, 'Navigate', [487, 555], 'Core routing method that resolves a URI or parameter to a page type and navigates the active frame.', ['navigation', 'routing'], 'complex');
addFunction(p14, 'NavigateInCurrentTab', [605, 630], 'Navigates within the current tab frame, updating the tab title and icon.', ['navigation', 'tab'], 'moderate');
addFunction(p14, 'AddNewTab', [632, 637], 'Creates a new tab and navigates it to the given parameter.', ['navigation', 'tab'], 'simple');
addFunction(p14, 'CreateTab', [639, 658], 'Constructs a TabBarItem with title, icon, and Frame configured for the navigation stack.', ['navigation', 'tab', 'component'], 'moderate');
addFunction(p14, 'CreateIconSource', [660, 734], 'Resolves a navigation parameter to an appropriate IconSource (glyph, image, or default).', ['utility', 'ui', 'navigation'], 'complex');
addFunction(p14, 'GetDefaultHeader', [736, 778], 'Computes a default tab header string from a navigation parameter type.', ['utility', 'navigation'], 'moderate');

// 15. PodcastPlaybackNavigation.cs
const p15 = 'src/Wavee.UI.WinUI/Helpers/Navigation/PodcastPlaybackNavigation.cs';
addFile(p15, 'Opens the episode page for the currently playing podcast episode by inspecting active playback state.', ['navigation', 'playback', 'utility'], 'moderate');
addClass(p15, 'PodcastPlaybackNavigation', [1, 58], 'Static helper that resolves the active episode URI from playback state and navigates to its episode page.', ['navigation', 'playback', 'utility'], 'moderate');
addFunction(p15, 'TryOpenCurrentEpisode', [10, 42], 'Reads the current playback cluster state and navigates to the active episode page if available.', ['navigation', 'playback'], 'moderate');

// 16. PageEntranceFade.cs
const p16 = 'src/Wavee.UI.WinUI/Helpers/PageEntranceFade.cs';
addFile(p16, 'Attached behavior that fades a page or element in on load using a composition opacity animation.', ['animation', 'attached-behavior', 'ui', 'component'], 'moderate');
addClass(p16, 'PageEntranceFade', [1, 109], 'Attached property that triggers a fade-in composition animation when the host element loads.', ['animation', 'attached-behavior', 'component'], 'moderate');
addFunction(p16, 'OnFadeChanged', [65, 79], 'Attaches or detaches Loaded/Unloaded handlers when the Fade property is set.', ['event-handler', 'animation'], 'simple');
addFunction(p16, 'OnLoaded', [81, 97], 'Starts the fade-in opacity animation via the composition visual when the element loads.', ['animation', 'event-handler'], 'moderate');

// 17. MediaTracksMenuBuilder.cs
const p17 = 'src/Wavee.UI.WinUI/Helpers/Playback/MediaTracksMenuBuilder.cs';
addFile(p17, 'Builds a WinUI flyout menu listing available audio and subtitle tracks from the active playback session.', ['ui', 'playback', 'component', 'factory'], 'complex');
addClass(p17, 'MediaTracksMenuBuilder', [1, 163], 'Populates a MenuFlyout with selectable audio/subtitle track items sourced from active playback metadata.', ['ui', 'playback', 'factory', 'component'], 'complex');
addFunction(p17, 'TryPopulateFromActivePlayback', [27, 35], 'Convenience entry point that fetches current playback state and populates the flyout.', ['playback', 'ui'], 'simple');
addFunction(p17, 'Populate', [43, 140], 'Builds all track menu items including audio language options and subtitle toggles.', ['ui', 'playback', 'factory'], 'complex');

// 18. PlaybackSaveTargetResolver.cs
const p18 = 'src/Wavee.UI.WinUI/Helpers/Playback/PlaybackSaveTargetResolver.cs';
addFile(p18, 'Resolves the current playback context to a concrete track or episode URI suitable for library save/like operations.', ['playback', 'utility', 'library'], 'moderate');
addClass(p18, 'PlaybackSaveTargetResolver', [1, 136], 'Static resolver that extracts a normalized Spotify URI from various playback state representations.', ['playback', 'utility', 'library'], 'moderate');
addFunction(p18, 'ResolveTrackUriAsync', [79, 125], 'Async resolution path that queries the SpClient when the local state is insufficient to determine the track URI.', ['playback', 'async', 'library'], 'complex');
addFunction(p18, 'GetEpisodeUri', [47, 77], 'Extracts the episode URI from a podcast playback state including fallback resolution.', ['playback', 'utility'], 'moderate');

// 19. SpotifyVideoQualityFlyout.cs
const p19 = 'src/Wavee.UI.WinUI/Helpers/Playback/SpotifyVideoQualityFlyout.cs';
addFile(p19, 'Builds and shows a flyout menu of available Spotify video quality options for the current stream.', ['ui', 'playback', 'video', 'component'], 'moderate');
addClass(p19, 'SpotifyVideoQualityFlyout', [1, 96], 'Static helper that creates a MenuFlyout with selectable video quality items and anchors it to a given element.', ['ui', 'playback', 'video'], 'moderate');
addFunction(p19, 'ShowAt', [11, 87], 'Creates and shows the quality selection flyout anchored to the provided UIElement.', ['ui', 'playback', 'video'], 'complex');

// 20. VideoSurfaceMorph.cs
const p20 = 'src/Wavee.UI.WinUI/Helpers/Playback/VideoSurfaceMorph.cs';
addFile(p20, 'Manages ConnectedAnimation transitions between the mini-player, full-screen, and gripper video surface states.', ['animation', 'video', 'connected-animation', 'playback'], 'complex');
addClass(p20, 'VideoSurfaceMorph', [1, 119], 'Helper coordinating prepare/start pairs of ConnectedAnimations for every video surface transition.', ['animation', 'video', 'connected-animation'], 'complex');
addFunction(p20, 'Prepare', [85, 104], 'Generalized prepare step that registers named elements on the ConnectedAnimationService.', ['animation', 'video'], 'moderate');
addFunction(p20, 'TryStart', [106, 118], 'Attempts to start a named connected animation on the destination element with fallback.', ['animation', 'video'], 'moderate');

// 21. PlaylistCoverHelper.cs
const p21 = 'src/Wavee.UI.WinUI/Helpers/PlaylistCoverHelper.cs';
addFile(p21, 'Encodes a user-picked image file to JPEG and prepares the base64 payload for the playlist cover upload API.', ['utility', 'image-processing', 'playlist', 'api-handler'], 'moderate');
addClass(p21, 'PlaylistCoverHelper', [1, 95], 'Async helper that opens a file picker, reads the image, and JPEG-encodes it for upload.', ['utility', 'image-processing', 'playlist'], 'moderate');
addFunction(p21, 'PrepareForUploadAsync', [32, 60], 'Prompts the user to pick an image file and encodes it as JPEG bytes for upload.', ['utility', 'image-processing', 'async'], 'moderate');
addFunction(p21, 'EncodeJpegAsync', [62, 94], 'Transcodes a StorageFile to a JPEG byte array using BitmapEncoder.', ['utility', 'image-processing', 'async'], 'moderate');

// 22. PodcastCommentReactionsDialog.cs
const p22 = 'src/Wavee.UI.WinUI/Helpers/PodcastCommentReactionsDialog.cs';
addFile(p22, 'Programmatically builds and displays a ContentDialog showing podcast comment reactions grouped by type with filter buttons.', ['ui', 'component', 'dialog', 'podcast'], 'complex');
addClass(p22, 'PodcastCommentReactionsDialog', [1, 270], 'Factory that constructs a fully custom reaction dialog with emoji counts, filter chips, and reaction rows.', ['ui', 'component', 'dialog', 'podcast'], 'complex');
addFunction(p22, 'ShowAsync', [25, 188], 'Builds the dialog UI programmatically and awaits user dismissal.', ['ui', 'component', 'dialog'], 'complex');
addFunction(p22, 'BuildFilterButton', [190, 207], 'Creates a chip-style ToggleButton for filtering reactions by emoji type.', ['ui', 'component', 'factory'], 'moderate');
addFunction(p22, 'BuildReactionRow', [209, 266], 'Constructs a single reaction row showing the user avatar, name, and emoji reaction.', ['ui', 'component', 'factory'], 'moderate');

// 23. ShimmerLoadGate.cs
const p23 = 'src/Wavee.UI.WinUI/Helpers/ShimmerLoadGate.cs';
addFile(p23, 'Orchestrates a shimmer-to-content crossfade for page sections: shows shimmer while loading, then fades it out when content is ready.', ['animation', 'ui', 'loading', 'component'], 'complex');
addClass(p23, 'ShimmerLoadGate', [1, 148], 'Tracks multiple shimmer+content element pairs and executes a staggered crossfade when all are marked ready.', ['animation', 'ui', 'loading', 'component'], 'complex');
addFunction(p23, 'RunCrossfadeAsync', [56, 89], 'Executes the shimmer fade-out and content fade-in composition animation.', ['animation', 'loading', 'async'], 'moderate');
addFunction(p23, 'Reset', [103, 140], 'Resets all tracked elements to shimmer-visible state for a new load cycle.', ['animation', 'loading'], 'moderate');

// 24. SidebarTreeBuilder.cs
const p24 = 'src/Wavee.UI.WinUI/Helpers/Sidebar/SidebarTreeBuilder.cs';
addFile(p24, 'Constructs the NavigationView sidebar tree nodes from the user library state including library sections and pinned items.', ['ui', 'sidebar', 'navigation', 'factory'], 'complex');
addClass(p24, 'SidebarTreeBuilder', [1, 135], 'Builder that assembles the hierarchical sidebar NavigationViewItem tree for the WinUI shell.', ['ui', 'sidebar', 'navigation', 'factory'], 'complex');
addFunction(p24, 'Build', [23, 80], 'Constructs top-level sidebar nodes including Home, Search, and Your Library root.', ['ui', 'sidebar', 'navigation'], 'moderate');
addFunction(p24, 'BuildYourLibraryChildren', [82, 134], 'Builds the expandable Your Library sub-tree with pinned items and library section nodes.', ['ui', 'sidebar', 'navigation', 'factory'], 'moderate');

// 25. TintColorHelper.cs
const p25 = 'src/Wavee.UI.WinUI/Helpers/TintColorHelper.cs';
addFile(p25, 'Provides color math utilities for deriving tint colors: brightening, blending toward a target, computing light tints, and parsing hex strings.', ['utility', 'color', 'ui', 'theming'], 'moderate');
addClass(p25, 'TintColorHelper', [1, 87], 'Static helper with color manipulation methods used to generate dynamic page-background tints from album art.', ['utility', 'color', 'theming'], 'moderate');
addFunction(p25, 'BrightenForTint', [19, 29], 'Scales HSL lightness of a color upward for use as a subtle background tint.', ['utility', 'color'], 'simple');
addFunction(p25, 'BlendToward', [36, 44], 'Linearly interpolates between two colors by a given factor.', ['utility', 'color'], 'simple');
addFunction(p25, 'TryParseHex', [56, 86], 'Parses a hex color string (#RRGGBB or #AARRGGBB) into a Windows.UI.Color.', ['utility', 'color'], 'moderate');

const result = {nodes, edges};
console.log('nodeCount:', nodes.length, 'edgeCount:', edges.length);

// Decide split: nodes<=60 AND edges<=120 => single file
// nodes=104, edges=79 -> need split since nodes>60
// parts = ceil(max(104/60, 79/120)) = ceil(max(1.73, 0.66)) = ceil(1.73) = 2
const nodeCount = nodes.length;
const edgeCount = edges.length;
console.log('Need split:', nodeCount > 60 || edgeCount > 120);

// Write output
fs.mkdirSync('C:/WAVEE/WaveeMusic/.understand-anything/intermediate', {recursive: true});

if (nodeCount <= 60 && edgeCount <= 120) {
  fs.writeFileSync('C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-42.json', JSON.stringify(result, null, 2), {encoding: 'utf8'});
  console.log('Written single file batch-42.json');
} else {
  // Split into 2 parts
  const parts = Math.ceil(Math.max(nodeCount / 60, edgeCount / 120));
  console.log('parts:', parts);

  // Get unique file paths in batch, sort alphabetically
  const allPaths = [
    'src/Wavee.UI.WinUI/Helpers/Application/RetryHandler.cs',
    'src/Wavee.UI.WinUI/Helpers/Application/TitleBarHelper.cs',
    'src/Wavee.UI.WinUI/Helpers/ConnectedAnimationHelper.cs',
    'src/Wavee.UI.WinUI/Helpers/ContentPageController.cs',
    'src/Wavee.UI.WinUI/Helpers/Debouncer.cs',
    'src/Wavee.UI.WinUI/Helpers/ErrorMapper.cs',
    'src/Wavee.UI.WinUI/Helpers/HeroCardAnimations.cs',
    'src/Wavee.UI.WinUI/Helpers/IContentPageHost.cs',
    'src/Wavee.UI.WinUI/Helpers/LocalImagePaletteHelper.cs',
    'src/Wavee.UI.WinUI/Helpers/Lyrics/LyricsContentParser.cs',
    'src/Wavee.UI.WinUI/Helpers/Lyrics/TranscriptToLyricsMapper.cs',
    'src/Wavee.UI.WinUI/Helpers/Navigation/AlbumNavigationHelper.cs',
    'src/Wavee.UI.WinUI/Helpers/Navigation/CreatePlaylistParameter.cs',
    'src/Wavee.UI.WinUI/Helpers/Navigation/NavigationHelpers.cs',
    'src/Wavee.UI.WinUI/Helpers/Navigation/PodcastPlaybackNavigation.cs',
    'src/Wavee.UI.WinUI/Helpers/PageEntranceFade.cs',
    'src/Wavee.UI.WinUI/Helpers/Playback/MediaTracksMenuBuilder.cs',
    'src/Wavee.UI.WinUI/Helpers/Playback/PlaybackSaveTargetResolver.cs',
    'src/Wavee.UI.WinUI/Helpers/Playback/SpotifyVideoQualityFlyout.cs',
    'src/Wavee.UI.WinUI/Helpers/Playback/VideoSurfaceMorph.cs',
    'src/Wavee.UI.WinUI/Helpers/PlaylistCoverHelper.cs',
    'src/Wavee.UI.WinUI/Helpers/PodcastCommentReactionsDialog.cs',
    'src/Wavee.UI.WinUI/Helpers/ShimmerLoadGate.cs',
    'src/Wavee.UI.WinUI/Helpers/Sidebar/SidebarTreeBuilder.cs',
    'src/Wavee.UI.WinUI/Helpers/TintColorHelper.cs'
  ].sort();

  const chunkSize = Math.ceil(allPaths.length / parts);

  for (let k = 1; k <= parts; k++) {
    const partPaths = new Set(allPaths.slice((k-1)*chunkSize, k*chunkSize));
    const partNodes = nodes.filter(n => {
      const fp = n.filePath || '';
      return partPaths.has(fp);
    });
    const partNodeIds = new Set(partNodes.map(n => n.id));
    const partEdges = edges.filter(e => partNodeIds.has(e.source));

    const partResult = {nodes: partNodes, edges: partEdges};
    const outPath = 'C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-42-part-' + k + '.json';
    fs.writeFileSync(outPath, JSON.stringify(partResult, null, 2), {encoding: 'utf8'});
    console.log('Written part', k, '- nodes:', partNodes.length, 'edges:', partEdges.length);
  }
}
