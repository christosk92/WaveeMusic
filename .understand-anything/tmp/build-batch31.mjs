import { writeFileSync } from 'fs';

const nodes = [];
const edges = [];

function fileNode(path, summary, tags, complexity, langNotes) {
  const name = path.split('/').pop();
  const n = { id: 'file:' + path, type: 'file', name, filePath: path, summary, tags, complexity };
  if (langNotes) n.languageNotes = langNotes;
  nodes.push(n);
}

function classNode(path, name, lineRange, summary, tags, complexity) {
  nodes.push({ id: 'class:' + path + ':' + name, type: 'class', name, filePath: path, lineRange, summary, tags, complexity });
  edges.push({ source: 'file:' + path, target: 'class:' + path + ':' + name, type: 'contains', direction: 'forward', weight: 1.0 });
  edges.push({ source: 'file:' + path, target: 'class:' + path + ':' + name, type: 'exports', direction: 'forward', weight: 0.8 });
}

function funcNode(path, name, lineRange, summary, tags, complexity) {
  nodes.push({ id: 'function:' + path + ':' + name, type: 'function', name, filePath: path, lineRange, summary, tags, complexity });
  edges.push({ source: 'file:' + path, target: 'function:' + path + ':' + name, type: 'contains', direction: 'forward', weight: 1.0 });
}

const P = 'src/Wavee.UI.WinUI/Controls/Playback/';
const PL = 'src/Wavee.UI.WinUI/Controls/PlayerBar/';
const PLS = 'src/Wavee.UI.WinUI/Controls/Playlist/';
const POD = 'src/Wavee.UI.WinUI/Controls/PodcastBrowse/';
const Q = 'src/Wavee.UI.WinUI/Controls/Queue/';
const REC = 'src/Wavee.UI.WinUI/Controls/';
const REO = 'src/Wavee.UI.WinUI/Controls/Reorder/';
const RP = 'src/Wavee.UI.WinUI/Controls/RightPanel/';
const RPD = 'src/Wavee.UI.WinUI/Controls/RightPanel/Details/';

// 1. AudioOutputPicker.xaml
fileNode(P+'AudioOutputPicker.xaml', 'XAML template for the audio output device picker flyout, defining volume sliders, device rows, compact/expanded modes, and Connect/Local device sections.', ['component', 'xaml', 'playback', 'audio-output'], 'complex');

// 2. AudioOutputPicker.xaml.cs
fileNode(P+'AudioOutputPicker.xaml.cs', 'Code-behind for the audio output picker: manages volume sliders, Spotify Connect device list, compact/expanded display modes, and device-switching via IPlaybackService.', ['component', 'playback', 'audio-output', 'spotify-connect'], 'complex');
classNode(P+'AudioOutputPicker.xaml.cs', 'AudioOutputPicker', [28,682], 'WinUI UserControl rendering a picker flyout with local and Spotify Connect output devices, handling volume drag, device selection, and compact-chrome layout modes.', ['component', 'playback', 'audio-output', 'spotify-connect'], 'complex');
classNode(P+'AudioOutputPicker.xaml.cs', 'AudioOutputDeviceRowViewModel', [684,720], 'View model for a single device row in AudioOutputPicker, tracking active/switching state and computing row brush/visibility.', ['data-model', 'playback', 'spotify-connect'], 'simple');
funcNode(P+'AudioOutputPicker.xaml.cs', 'RebuildRows', [486,546], 'Rebuilds local and Connect device row lists from current playback state, assigning icons and active flags to each row.', ['playback', 'audio-output', 'component'], 'moderate');
funcNode(P+'AudioOutputPicker.xaml.cs', 'UpdateSummary', [342,389], 'Updates the compact summary display with current device name, icon glyph, and active-track artist/title.', ['playback', 'audio-output', 'component'], 'moderate');
funcNode(P+'AudioOutputPicker.xaml.cs', 'ApplyCompactChrome', [304,335], 'Switches between compact icon-only and full-width display modes, adjusting grid column widths and control margins.', ['component', 'layout', 'playback'], 'moderate');
funcNode(P+'AudioOutputPicker.xaml.cs', 'DeviceRow_Click', [566,608], 'Handles device row click by transferring or switching audio output via IPlaybackService, then closing the picker flyout.', ['event-handler', 'playback', 'spotify-connect'], 'moderate');

// 3. CompositionProgressBar.cs
fileNode(P+'CompositionProgressBar.cs', 'GPU-accelerated progress/seek bar using WinUI Composition APIs for smooth animation, chapter segments, drag-to-seek, and a floating chapter tooltip.', ['component', 'playback', 'composition', 'seek-bar'], 'complex', 'Uses Windows.UI.Composition SpriteVisual and ExpressionAnimation for GPU-resident fill visuals; chapter segments are rendered as separate Grid columns with independent fill brushes.');
classNode(P+'CompositionProgressBar.cs', 'CompositionProgressBar', [35,778], 'Custom TemplatedControl implementing a seek/progress bar backed by Composition visuals, supporting segmented chapter markers, hover expand/collapse, drag-to-seek with snap threshold, and a chapter tooltip popup.', ['component', 'playback', 'composition', 'seek-bar'], 'complex');
funcNode(P+'CompositionProgressBar.cs', 'CompositionProgressBar', [83,175], 'Constructor: registers dependency properties, wires pointer events, and sets up the Composition root visual hierarchy.', ['component', 'playback', 'entry-point'], 'complex');
funcNode(P+'CompositionProgressBar.cs', 'RebuildSegments', [274,319], 'Destroys and recreates Grid columns and Composition visuals for each chapter segment when the Segments collection changes.', ['component', 'composition', 'playback'], 'moderate');
funcNode(P+'CompositionProgressBar.cs', 'ApplyGlobalFill', [418,450], 'Drives the global progress fill visual via an ExpressionAnimation bound to PositionMs/DurationMs dependency properties.', ['composition', 'playback', 'animation'], 'moderate');
funcNode(P+'CompositionProgressBar.cs', 'ApplySegmentFill', [452,501], 'Drives per-segment fill visuals via ExpressionAnimations clamped to each chapter boundary, enabling per-segment progress coloring.', ['composition', 'playback', 'animation'], 'moderate');
funcNode(P+'CompositionProgressBar.cs', 'UpdateDragRatio', [592,633], 'Computes seek target ratio from pointer position during drag, snapping away from click-only gestures via a threshold distance.', ['event-handler', 'playback', 'seek-bar'], 'moderate');
funcNode(P+'CompositionProgressBar.cs', 'UpdateChapterTooltip', [655,731], 'Shows and positions a floating chapter tooltip popup near the pointer, displaying chapter title and time range while hovering.', ['component', 'playback', 'tooltip'], 'complex');

// 4. PlaybackActionContent.xaml
fileNode(P+'PlaybackActionContent.xaml', 'Minimal XAML skeleton for PlaybackActionContent, a placeholder control hosting playback action button content.', ['component', 'xaml', 'playback'], 'simple');

// 5. PlaybackActionContent.xaml.cs
fileNode(P+'PlaybackActionContent.xaml.cs', 'Code-behind for PlaybackActionContent UserControl, exposing dependency properties for binding playback action state to a button icon/label.', ['component', 'playback', 'dependency-properties'], 'moderate');
classNode(P+'PlaybackActionContent.xaml.cs', 'PlaybackActionContent', [8,180], 'UserControl with dependency properties for a single playback action (icon glyph, label, active state) used in the transport controls.', ['component', 'playback', 'dependency-properties'], 'moderate');

// 6. TransportModeButton.cs
fileNode(P+'TransportModeButton.cs', 'Custom ToggleButton subclass for Repeat/Shuffle transport mode states, mapping enum values to XAML visual states.', ['component', 'playback', 'transport-controls'], 'simple');
classNode(P+'TransportModeButton.cs', 'TransportModeButton', [11,95], 'ToggleButton-derived control that cycles through transport mode states (Off, One, All) via dependency properties and visual state transitions.', ['component', 'playback', 'transport-controls'], 'moderate');

// 7. VideoTransportBar.xaml
fileNode(P+'VideoTransportBar.xaml', 'XAML layout for the video transport bar overlay, including surface mode toggles, fullscreen/miniplayer buttons, volume controls, and chapter track lists.', ['component', 'xaml', 'playback', 'video'], 'moderate');

// 8. VideoTransportBar.xaml.cs
fileNode(P+'VideoTransportBar.xaml.cs', 'Code-behind for the video transport bar control, handling surface-mode switching, fullscreen and mini-player navigation, and volume glyph updates.', ['component', 'playback', 'video', 'event-handler'], 'moderate');
classNode(P+'VideoTransportBar.xaml.cs', 'VideoTransportBar', [42,318], 'UserControl rendering the floating transport bar for the video playback surface, with surface-mode buttons, seek bar, volume, fullscreen/mini-player menu items, and tracklist flyout.', ['component', 'playback', 'video'], 'complex');
funcNode(P+'VideoTransportBar.xaml.cs', 'ApplySurfaceMode', [102,122], 'Toggles the control layout between inline player bar and fullscreen overlay modes by updating visual state and binding visibility.', ['component', 'playback', 'layout'], 'simple');
funcNode(P+'VideoTransportBar.xaml.cs', 'FullscreenMenuItem_Click', [281,299], 'Navigates to the fullscreen video view when the user selects the fullscreen menu item.', ['event-handler', 'playback', 'video'], 'simple');
funcNode(P+'VideoTransportBar.xaml.cs', 'MiniPlayerMenuItem_Click', [301,317], 'Navigates to the mini-player view when the user selects the mini-player menu item.', ['event-handler', 'playback', 'video'], 'simple');

// 9. WatchVideoButton.xaml
fileNode(P+'WatchVideoButton.xaml', 'XAML template for the Watch Video pill button shown in the player bar when a music video is available.', ['component', 'xaml', 'playback', 'video'], 'simple');

// 10. WatchVideoButton.xaml.cs
fileNode(P+'WatchVideoButton.xaml.cs', 'Minimal code-behind for WatchVideoButton, a ToggleButton subclass that triggers transition to the video playback surface.', ['component', 'playback', 'video'], 'simple');
classNode(P+'WatchVideoButton.xaml.cs', 'WatchVideoButton', [15,55], 'ToggleButton subclass rendering the Watch/Audio toggle pill in the player bar, exposing dependency properties for video availability state.', ['component', 'playback', 'video'], 'simple');

// 11. PlayerBar.xaml
fileNode(PL+'PlayerBar.xaml', 'Main player bar XAML layout (~1119 lines) composing the now-playing track info, transport controls, progress bar, heart button, device picker, volume, and overflow menu.', ['component', 'xaml', 'playback', 'player-bar'], 'complex');

// 12. PlayerBar.xaml.cs
fileNode(PL+'PlayerBar.xaml.cs', 'Code-behind for the main PlayerBar control: wires playback state subscriptions, applies tint color, handles heart toggle, navigate-to-album/context, drag-drop subtitle files, video popout teaching tip, and overflow menu items.', ['component', 'playback', 'player-bar', 'event-handler'], 'complex');
classNode(PL+'PlayerBar.xaml.cs', 'PlayerBar', [30,796], 'UserControl anchoring the full now-playing bar: subscribes to IPlaybackStateService events, renders track metadata with accent tinting, handles seek/skip/heart/queue interactions, and manages the video popout teaching tip.', ['component', 'playback', 'player-bar'], 'complex');
funcNode(PL+'PlayerBar.xaml.cs', 'ApplyTintColor', [374,423], 'Applies an accent tint extracted from the current track artwork to PlayerBar foreground brushes and backgrounds using Composition color animations.', ['playback', 'player-bar', 'composition', 'animation'], 'complex');
funcNode(PL+'PlayerBar.xaml.cs', 'NavigateToAlbum', [463,518], 'Navigates to the album detail page for the currently playing track, resolving album context from the playback state.', ['event-handler', 'playback', 'navigation'], 'complex');
funcNode(PL+'PlayerBar.xaml.cs', 'NavigateToActiveContext', [520,577], 'Navigates to the active playback context (playlist, album, artist, or show page) based on the current playback state context URI.', ['event-handler', 'playback', 'navigation'], 'complex');
funcNode(PL+'PlayerBar.xaml.cs', 'PlayerBarRoot_DragOver', [667,709], 'Validates drag-over events on the player bar, accepting only subtitle file drops (SRT, VTT, ASS) for the current video track.', ['event-handler', 'playback', 'drag-drop'], 'moderate');
funcNode(PL+'PlayerBar.xaml.cs', 'PlayerBarRoot_Drop', [716,768], 'Handles subtitle file drops onto the player bar, loading the first valid subtitle file and forwarding it to the playback service.', ['event-handler', 'playback', 'drag-drop'], 'moderate');
funcNode(PL+'PlayerBar.xaml.cs', 'SubscribeEvents', [105,124], 'Subscribes to IPlaybackStateService property-changed events for layout and heart state updates.', ['event-handler', 'playback', 'player-bar'], 'simple');

// 13. PlaylistShyPill.xaml
fileNode(PLS+'PlaylistShyPill.xaml', 'XAML layout for the playlist shy-pill control shown at the bottom of the playlist page during playback.', ['component', 'xaml', 'playlist'], 'simple');

// 14. PlaylistShyPill.xaml.cs
fileNode(PLS+'PlaylistShyPill.xaml.cs', 'Code-behind for PlaylistShyPill, a compact collapsible playback summary pill that appears at the base of the playlist page.', ['component', 'playlist', 'playback'], 'simple');
classNode(PLS+'PlaylistShyPill.xaml.cs', 'PlaylistShyPill', [17,92], 'UserControl rendering a shy-pill transport summary at the bottom of the playlist view, with dependency properties for track title, artist, and playback state.', ['component', 'playlist', 'playback'], 'moderate');

// 15. PodcastBrowseSectionTemplateSelector.cs
fileNode(POD+'PodcastBrowseSectionTemplateSelector.cs', 'DataTemplateSelector for the podcast browse view, choosing between episode-list and show-header templates based on section type.', ['component', 'podcast', 'template-selector'], 'simple');
classNode(POD+'PodcastBrowseSectionTemplateSelector.cs', 'PodcastBrowseSectionTemplateSelector', [16,43], 'DataTemplateSelector subclass mapping podcast browse section view model types to their corresponding XAML data templates.', ['component', 'podcast', 'template-selector'], 'simple');

// 16. QueueControl.xaml
fileNode(Q+'QueueControl.xaml', 'XAML layout for the queue panel (~462 lines) with now-playing header, user-queue section, context-queue section, drag-drop reorder handles, and an empty-queue state.', ['component', 'xaml', 'queue', 'playback'], 'complex');

// 17. QueueControl.xaml.cs
fileNode(Q+'QueueControl.xaml.cs', 'Code-behind for the queue panel: manages drag-drop reorder between user-queue and context-queue lists, issues queue-mutation IPC commands, and displays a live queue snapshot from IPlaybackStateService.', ['component', 'queue', 'playback', 'drag-drop'], 'complex');
classNode(Q+'QueueControl.xaml.cs', 'QueueDisplayItem', [37,93], 'Wrapper view model for a queue entry, adding a display index and computed IsCurrentTrack flag on top of the underlying track data.', ['data-model', 'queue', 'playback'], 'simple');
classNode(Q+'QueueControl.xaml.cs', 'QueueControl', [95,931], 'UserControl rendering the right-panel queue view with reorderable user-queue and context-queue lists, supporting drag-drop reorder, remove, add-to-queue, and now-playing highlight.', ['component', 'queue', 'playback', 'drag-drop'], 'complex');

// 18. RecentlyPlayedSection.xaml
fileNode(REC+'RecentlyPlayedSection.xaml', 'XAML layout for the recently-played shelf section, rendering a horizontal scroll row of content cards for recently played items.', ['component', 'xaml', 'recently-played'], 'simple');

// 19. RecentlyPlayedSection.xaml.cs
fileNode(REC+'RecentlyPlayedSection.xaml.cs', 'Code-behind for RecentlyPlayedSection, loading recently played items from ISpotifyRecentlyPlayedService and binding them to a content card shelf.', ['component', 'recently-played', 'service'], 'simple');
classNode(REC+'RecentlyPlayedSection.xaml.cs', 'RecentlyPlayedSection', [9,108], 'UserControl that fetches and displays the recently played shelf, lazily loading data on first visual-tree attachment.', ['component', 'recently-played'], 'moderate');

// 20. ReorderDropIndicator.cs
fileNode(REO+'ReorderDropIndicator.cs', 'Visual drop-indicator control shown during drag-reorder operations in list views, rendering an insertion line at the target position.', ['component', 'drag-drop', 'reorder'], 'moderate');
classNode(REO+'ReorderDropIndicator.cs', 'ReorderDropIndicator', [23,151], 'Custom control rendering a highlighted insertion line for drag-and-drop reorder operations, with animated visibility transitions.', ['component', 'drag-drop', 'reorder'], 'moderate');

// 21. RhythmBreakBanner.xaml
fileNode(REC+'RhythmBreakBanner.xaml', 'XAML layout for the RhythmBreak banner shown for live/radio tracks, featuring a pulsing live indicator and track meta.', ['component', 'xaml', 'live-radio', 'playback'], 'moderate');

// 22. RhythmBreakBanner.xaml.cs
fileNode(REC+'RhythmBreakBanner.xaml.cs', 'Code-behind for RhythmBreakBanner: drives a pulse animation on the live indicator via Composition scale animations and starts/stops it based on IsLive state.', ['component', 'live-radio', 'composition', 'animation'], 'moderate');
classNode(REC+'RhythmBreakBanner.xaml.cs', 'RhythmBreakBanner', [24,245], 'UserControl for a live-radio event banner with Composition-driven pulse animation on the live badge and dependency properties for track title, artist, and live state.', ['component', 'live-radio', 'composition'], 'moderate');
funcNode(REC+'RhythmBreakBanner.xaml.cs', 'StartPulse', [134,167], 'Creates and starts a looping Composition scale animation on the live indicator dot to produce a pulsing live effect.', ['animation', 'composition', 'live-radio'], 'moderate');
funcNode(REC+'RhythmBreakBanner.xaml.cs', 'StopPulse', [169,184], 'Stops all Composition animations on the live indicator and resets the scale to resting state.', ['animation', 'composition', 'live-radio'], 'simple');

// 23. BackgroundOverlayCompositionBehavior.cs
fileNode(RP+'BackgroundOverlayCompositionBehavior.cs', 'Attached Behavior that applies a GPU Composition background color overlay and a tab-content edge-fade to the right-panel host, transitioning colors smoothly when the active tab or track changes.', ['component', 'composition', 'right-panel', 'behavior'], 'complex', 'Uses Windows.UI.Composition SpriteVisual with ColorBrush and mask-based InsetClip for the edge-fade overlay; all color transitions are ExpressionAnimation-driven for GPU-resident smoothness.');
classNode(RP+'BackgroundOverlayCompositionBehavior.cs', 'BackgroundOverlayCompositionBehavior', [46,447], 'Behavior class attaching GPU Composition overlays to the right panel, providing background color tinting driven by current track artwork and a content-edge fade for tab transitions.', ['component', 'composition', 'right-panel', 'behavior'], 'complex');
funcNode(RP+'BackgroundOverlayCompositionBehavior.cs', 'EnsureBackgroundOverlayComposition', [234,320], 'Creates or recreates the SpriteVisual for the background overlay, wiring up size binding and color animation to the host element.', ['composition', 'right-panel', 'animation'], 'complex');
funcNode(RP+'BackgroundOverlayCompositionBehavior.cs', 'EnsureTabContentFadeComposition', [365,413], 'Creates the edge-fade mask composition on the tab content area to produce a soft fade-out at the bottom of the right panel.', ['composition', 'right-panel', 'animation'], 'moderate');
funcNode(RP+'BackgroundOverlayCompositionBehavior.cs', 'ApplyState', [171,200], 'Applies the current overlay state (colors, visibility) to the Composition visuals, interpolating between previous and new accent colors.', ['composition', 'right-panel', 'animation'], 'moderate');

// 24. DetailsTabHost.xaml
fileNode(RPD+'DetailsTabHost.xaml', 'XAML layout (~798 lines) for the right-panel details tab host, combining lyrics, canvas art, AI meaning, podcast chapters, comments, and episode metadata tabs into a single scrollable pane.', ['component', 'xaml', 'right-panel', 'details'], 'complex');

// 25. DetailsTabHost.xaml.cs
fileNode(RPD+'DetailsTabHost.xaml.cs', 'Code-behind for DetailsTabHost (~2922 lines): orchestrates the right-panel detail view for tracks, podcasts, and canvas art; manages lyrics scrolling, AI meaning requests, canvas video playback, podcast chapters/comments, and an on-device Phi Silica lyrics analysis pipeline.', ['component', 'right-panel', 'details', 'lyrics', 'ai'], 'complex');
classNode(RPD+'DetailsTabHost.xaml.cs', 'DetailsTabHost', [79,2922], 'Large UserControl acting as the right-panel details host: renders synced lyrics, Spotify Canvas video, AI-generated song meaning (Phi Silica), podcast chapters and comments, and episode metadata, switching content reactively as playback state changes.', ['component', 'right-panel', 'details', 'lyrics', 'ai'], 'complex');
funcNode(RPD+'DetailsTabHost.xaml.cs', 'InitializeDetails', [289,313], 'Wires up event subscriptions and fetches initial state when the details host is loaded and a playback context is available.', ['component', 'right-panel', 'details'], 'moderate');
funcNode(RPD+'DetailsTabHost.xaml.cs', 'TeardownDetails', [319,348], 'Unsubscribes all event handlers and releases composition resources when the details host is unloaded or the track context changes.', ['component', 'right-panel', 'details'], 'moderate');
funcNode(RPD+'DetailsTabHost.xaml.cs', 'UpdateDetailsContent', [549,630], 'Switches the details pane content between track, podcast, and local-media detail views in response to playback state changes.', ['component', 'right-panel', 'details'], 'complex');
funcNode(RPD+'DetailsTabHost.xaml.cs', 'UpdatePodcastDetailsContent', [632,691], 'Populates the podcast detail section with episode metadata, chapters, and comments when a podcast episode becomes active.', ['component', 'right-panel', 'podcast'], 'complex');
funcNode(RPD+'DetailsTabHost.xaml.cs', 'SyncDetailsAiMeaningForCurrentTrack', [1220,1250], 'Initiates or cancels an on-device Phi Silica AI meaning request for the current track, throttled to avoid re-running on rapid track changes.', ['ai', 'right-panel', 'lyrics'], 'moderate');
funcNode(RPD+'DetailsTabHost.xaml.cs', 'RenderDetailsAiMeaningWithCitations', [1337,1382], 'Renders AI-generated song meaning text with inline citation links as a rich XAML inline collection.', ['ai', 'right-panel', 'lyrics'], 'moderate');
funcNode(RPD+'DetailsTabHost.xaml.cs', 'BuildCanvasLyricPresentation', [1692,1779], 'Constructs a composition surface for rendering per-line lyric text over the Spotify Canvas video background.', ['composition', 'lyrics', 'right-panel'], 'complex');
funcNode(RPD+'DetailsTabHost.xaml.cs', 'RenderCanvasLyricSurface', [1781,1852], 'Draws the current lyric line onto a CanvasDrawingSession using Win2D, scaling the text to fit the canvas surface dimensions.', ['composition', 'lyrics', 'right-panel', 'win2d'], 'complex');
funcNode(RPD+'DetailsTabHost.xaml.cs', 'UpdatePodcastChaptersSource', [815,874], 'Fetches and binds podcast chapter data from the SpClient, computing chapter durations and current chapter highlight state.', ['component', 'right-panel', 'podcast'], 'complex');

console.log('Total nodes:', nodes.length, 'Total edges:', edges.length);

// Split into 2 parts: files alphabetically, first half in part 1, second half in part 2
// Part 1: Playback + PlayerBar files (indices 0-11 in path order)
// Part 2: Playlist + PodcastBrowse + Queue + RightPanel files (indices 12-24)

const allFiles = [
  P+'AudioOutputPicker.xaml', P+'AudioOutputPicker.xaml.cs',
  P+'CompositionProgressBar.cs',
  P+'PlaybackActionContent.xaml', P+'PlaybackActionContent.xaml.cs',
  P+'TransportModeButton.cs',
  P+'VideoTransportBar.xaml', P+'VideoTransportBar.xaml.cs',
  P+'WatchVideoButton.xaml', P+'WatchVideoButton.xaml.cs',
  PL+'PlayerBar.xaml', PL+'PlayerBar.xaml.cs',
  PLS+'PlaylistShyPill.xaml', PLS+'PlaylistShyPill.xaml.cs',
  POD+'PodcastBrowseSectionTemplateSelector.cs',
  Q+'QueueControl.xaml', Q+'QueueControl.xaml.cs',
  REC+'RecentlyPlayedSection.xaml', REC+'RecentlyPlayedSection.xaml.cs',
  REO+'ReorderDropIndicator.cs',
  REC+'RhythmBreakBanner.xaml', REC+'RhythmBreakBanner.xaml.cs',
  RP+'BackgroundOverlayCompositionBehavior.cs',
  RPD+'DetailsTabHost.xaml', RPD+'DetailsTabHost.xaml.cs'
];

const part1Files = new Set(allFiles.slice(0, 12));
const part2Files = new Set(allFiles.slice(12));

function nodeInFiles(n, fileSet) {
  const fp = n.filePath || (n.id.startsWith('file:') ? n.id.slice(5) : null);
  if (!fp) return false;
  return fileSet.has(fp);
}

const part1Nodes = nodes.filter(n => nodeInFiles(n, part1Files));
const part2Nodes = nodes.filter(n => nodeInFiles(n, part2Files));

const part1NodeIds = new Set(part1Nodes.map(n => n.id));
const part2NodeIds = new Set(part2Nodes.map(n => n.id));

const part1Edges = edges.filter(e => part1NodeIds.has(e.source));
const part2Edges = edges.filter(e => part2NodeIds.has(e.source));

console.log('Part1:', part1Nodes.length, 'nodes', part1Edges.length, 'edges');
console.log('Part2:', part2Nodes.length, 'nodes', part2Edges.length, 'edges');

writeFileSync(
  'C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-31-part-1.json',
  JSON.stringify({ nodes: part1Nodes, edges: part1Edges }, null, 2)
);

writeFileSync(
  'C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-31-part-2.json',
  JSON.stringify({ nodes: part2Nodes, edges: part2Edges }, null, 2)
);

console.log('Done writing batch-31-part-1.json and batch-31-part-2.json');
