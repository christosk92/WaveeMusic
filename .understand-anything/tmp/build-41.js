const fs = require('fs');
const nodes = [];
const edges = [];

function fileNode(path, summary, tags, complexity, languageNotes) {
  const n = { id: 'file:' + path, type: 'file', name: path.split('/').pop(), filePath: path, summary, tags, complexity };
  if (languageNotes) n.languageNotes = languageNotes;
  return n;
}
function classNode(path, name, lineRange, summary, tags, complexity) {
  return { id: 'class:' + path + ':' + name, type: 'class', name, filePath: path, lineRange, summary, tags, complexity };
}
function funcNode(path, name, lineRange, summary, tags, complexity) {
  return { id: 'function:' + path + ':' + name, type: 'function', name, filePath: path, lineRange, summary, tags, complexity };
}
function addContains(fp, id) {
  edges.push({ source: 'file:' + fp, target: id, type: 'contains', direction: 'forward', weight: 1.0 });
}
function addExports(fp, id) {
  edges.push({ source: 'file:' + fp, target: id, type: 'exports', direction: 'forward', weight: 0.8 });
}

// ExtendedMetadataStore.cs
const p0 = 'src/Wavee.UI.WinUI/Data/Stores/ExtendedMetadataStore.cs';
nodes.push(fileNode(p0, 'Two-tier read-through metadata store that batches outbound SpClient/Pathfinder requests with configurable window and size limits, serving hot (memory) and cold (disk) caches.', ['service','data-model','caching','batch-processing'], 'complex'));
const c0 = classNode(p0, 'ExtendedMetadataStore', [33,244], 'Implements a read-through cache with hot in-memory and cold persistent tiers, batching fetch requests to reduce API calls.', ['service','caching','data-model'], 'complex');
nodes.push(c0); addContains(p0, c0.id); addExports(p0, c0.id);
[
  ['FetchAsync',[67,112],'Fetches metadata for a key with batching, aggregating pending requests and fanning out to the underlying API client.',['function','service','caching'],'moderate'],
  ['GetOnceAsync',[121,131],'Retrieves a single metadata item by URI and kind, going through the two-tier cache.',['function','service','data-model'],'simple'],
  ['GetManyAsync',[133,161],'Fetches multiple metadata items in bulk, deduplicating keys and using the two-tier cache.',['function','service','batch-processing'],'moderate'],
  ['ResolveSingleAsync',[163,187],'Resolves a single item from batch results, handling missing data and cache writes.',['function','service','caching'],'moderate'],
  ['FlushDueBatchAsync',[191,202],'Triggers a flush of all pending batched requests that have exceeded the time window.',['function','service','batch-processing'],'simple'],
  ['FlushBatchAsync',[204,241],'Executes a single batch of pending metadata fetch requests against the API client.',['function','service','batch-processing'],'complex'],
].forEach(([name,lr,sum,tags,cx]) => {
  const fn = funcNode(p0, name, lr, sum, tags, cx);
  nodes.push(fn); addContains(p0, fn.id); addExports(p0, fn.id);
});

// PlaylistStore.cs
const p1 = 'src/Wavee.UI.WinUI/Data/Stores/PlaylistStore.cs';
nodes.push(fileNode(p1, 'Playlist-specific read-through data store wrapping the library data service and playlist cache, with change-detection via content hashing and partial-hint support.', ['service','data-model','caching'], 'complex'));
const c1 = classNode(p1, 'PlaylistStore', [27,159], 'Manages playlist data lifecycle including hot/cold cache tiers, content-hash based change detection, and partial-playlist hints.', ['service','data-model','caching'], 'complex');
nodes.push(c1); addContains(p1, c1.id); addExports(p1, c1.id);
[
  ['ShouldPublish',[76,103],'Determines whether a newly fetched playlist value should trigger a change notification by comparing content hashes.',['function','data-model','validation'],'moderate'],
  ['ComputeContentHash',[105,133],'Computes a stable content hash of a playlist value to detect meaningful changes between fetches.',['function','utility','serialization'],'moderate'],
  ['HintPartial',[145,150],'Injects a partial playlist update hint into the store, bypassing a full fetch cycle.',['function','service','data-model'],'simple'],
].forEach(([name,lr,sum,tags,cx]) => {
  const fn = funcNode(p1, name, lr, sum, tags, cx);
  nodes.push(fn); addContains(p1, fn.id); addExports(p1, fn.id);
});

// LiveInstanceTracker.cs
const p2 = 'src/Wavee.UI.WinUI/Diagnostics/LiveInstanceTracker.cs';
nodes.push(fileNode(p2, 'Weak-reference registry that tracks live WinUI page/control instances for memory diagnostics, providing point-in-time snapshots of alive objects.', ['service','diagnostics','utility'], 'moderate'));
const c2 = classNode(p2, 'LiveInstanceTracker', [15,70], 'Thread-safe weak-reference tracker exposing Register and Snapshot for live instance counting.', ['service','diagnostics'], 'moderate');
nodes.push(c2); addContains(p2, c2.id); addExports(p2, c2.id);
const f2a = funcNode(p2, 'Register', [25,38], 'Registers an object instance as a weak reference for alive-instance tracking.', ['function','service','diagnostics'], 'simple');
nodes.push(f2a); addContains(p2, f2a.id);
const f2b = funcNode(p2, 'Snapshot', [45,69], 'Returns a snapshot of all currently alive tracked instances, pruning dead weak references.', ['function','service','diagnostics'], 'moderate');
nodes.push(f2b); addContains(p2, f2b.id);

// MemoryDiagnosticsService.cs
const p3 = 'src/Wavee.UI.WinUI/Diagnostics/MemoryDiagnosticsService.cs';
nodes.push(fileNode(p3, 'Periodic memory sampler that captures GC heap, working set, and process metrics at a configurable interval and exports snapshots to CSV files.', ['service','diagnostics','monitoring'], 'complex'));
const c3 = classNode(p3, 'MemoryDiagnosticsService', [28,262], 'Implements timed memory sampling, metric capture, and CSV snapshot export for runtime memory diagnostics.', ['service','diagnostics','monitoring'], 'complex');
nodes.push(c3); addContains(p3, c3.id); addExports(p3, c3.id);
[
  ['Capture',[112,201],'Captures a full memory snapshot including GC generation sizes, working set, private bytes, and live instance counts.',['function','service','diagnostics'],'complex'],
  ['WriteSnapshotCsvAsync',[208,253],'Serializes a memory snapshot to a CSV file at the configured diagnostics path.',['function','service','monitoring'],'moderate'],
].forEach(([name,lr,sum,tags,cx]) => {
  const fn = funcNode(p3, name, lr, sum, tags, cx);
  nodes.push(fn); addContains(p3, fn.id); addExports(p3, fn.id);
});

// MemoryDiagnosticsViewModel.cs
const p4 = 'src/Wavee.UI.WinUI/Diagnostics/MemoryDiagnosticsViewModel.cs';
nodes.push(fileNode(p4, 'MVVM ViewModel for the memory diagnostics debug page, driving start/stop sampling, GC triggers, cache clears, and snapshot exports.', ['component','diagnostics','data-model'], 'complex'));
const c4 = classNode(p4, 'MemoryDiagnosticsViewModel', [22,185], 'Exposes memory diagnostic commands (ForceGc, ClearCaches, DropTabCache, Snapshot) and live chart samples to the debug UI.', ['component','diagnostics','data-model'], 'complex');
nodes.push(c4); addContains(p4, c4.id); addExports(p4, c4.id);
[
  ['OnSampled',[73,94],'Handles a new memory sample from MemoryDiagnosticsService, updating chart series and observable properties.',['function','event-handler','diagnostics'],'moderate'],
  ['ClearAllCachesAsync',[129,148],'Clears all image and data caches and triggers a GC, reporting before/after working set to the debug UI.',['function','service','diagnostics'],'moderate'],
  ['DropTabCache',[150,164],'Evicts the WinUI frame navigation cache and forces a GC collection cycle.',['function','service','diagnostics'],'moderate'],
].forEach(([name,lr,sum,tags,cx]) => {
  const fn = funcNode(p4, name, lr, sum, tags, cx);
  nodes.push(fn); addContains(p4, fn.id);
});

// NavigationDiagnostics.cs
const p5 = 'src/Wavee.UI.WinUI/Diagnostics/NavigationDiagnostics.cs';
nodes.push(fileNode(p5, 'Navigation profiler that records per-navigation stage timings, memory deltas, GC events, and UI stall detections, and generates structured reports.', ['service','diagnostics','monitoring'], 'complex', 'Uses P/Invoke to read PROCESS_MEMORY_COUNTERS for accurate per-nav GC delta accounting.'));
const c5 = classNode(p5, 'NavigationDiagnostics', [26,615], 'Tracks click-to-committed navigation timelines with stage breakdowns, working-set before/after, and GC accounting.', ['service','diagnostics','monitoring'], 'complex');
nodes.push(c5); addContains(p5, c5.id); addExports(p5, c5.id);
[
  ['BeginNav',[91,147],'Starts recording a new navigation event, capturing click-to-begin latency and initial memory counters.',['function','service','event-handler'],'complex'],
  ['EndNav',[184,230],'Finalises a navigation record, computes memory deltas, and logs a structured summary.',['function','service','monitoring'],'complex'],
  ['RecordMemoryRelease',[236,260],'Records a memory-release event (e.g. from page eviction) with GC gen2 and working-set deltas.',['function','service','diagnostics'],'moderate'],
  ['RecordGc',[269,299],'Records a GC collection event with generation, allocation delta, and memory state.',['function','service','diagnostics'],'moderate'],
  ['OnUiStallDetected',[307,355],'Handles a UI-thread stall detection event, logging frame number, duration, and GC deltas.',['function','event-handler','diagnostics'],'complex'],
  ['GenerateReport',[361,496],'Generates a comprehensive human-readable navigation performance report from all recorded events.',['function','service','monitoring'],'complex'],
].forEach(([name,lr,sum,tags,cx]) => {
  const fn = funcNode(p5, name, lr, sum, tags, cx);
  nodes.push(fn); addContains(p5, fn.id); addExports(p5, fn.id);
});

// WaveeNavigationEventSource.cs
const p6 = 'src/Wavee.UI.WinUI/Diagnostics/WaveeNavigationEventSource.cs';
nodes.push(fileNode(p6, 'ETW EventSource that emits structured navigation events (Navigating/Navigated) consumable by PerfView, WPA, and Windows Performance Recorder.', ['service','diagnostics','monitoring'], 'moderate'));
const c6 = classNode(p6, 'WaveeNavigationEventSource', [31,71], 'Singleton ETW EventSource providing Navigating and Navigated tracing events for navigation profiling.', ['service','diagnostics'], 'moderate');
nodes.push(c6); addContains(p6, c6.id); addExports(p6, c6.id);

// XfadeLog.cs
const p7 = 'src/Wavee.UI.WinUI/Diagnostics/XfadeLog.cs';
nodes.push(fileNode(p7, 'Thin static logger helper for crossfade pipeline events, emitting structured log messages with a consistent [xfade] tag.', ['utility','diagnostics'], 'simple'));
const c7 = classNode(p7, 'XfadeLog', [10,18], 'Provides a Tag helper method for logging crossfade pipeline steps.', ['utility','diagnostics'], 'simple');
nodes.push(c7); addContains(p7, c7.id);

// DragModifiers.cs
const p8 = 'src/Wavee.UI.WinUI/DragDrop/DragModifiers.cs';
nodes.push(fileNode(p8, 'Reads keyboard modifier key states at drag-start time to influence drag operation semantics (e.g. copy vs move).', ['utility','event-handler'], 'simple'));
const c8 = classNode(p8, 'DragModifiersCapture', [12,25], 'Captures the current keyboard modifier key states for use during drag operations.', ['utility','event-handler'], 'simple');
nodes.push(c8); addContains(p8, c8.id);

// DragOverlayHelper.cs
const p9 = 'src/Wavee.UI.WinUI/DragDrop/DragOverlayHelper.cs';
nodes.push(fileNode(p9, 'Animates a drop-zone overlay element with fade-in/out transitions to give visual feedback during drag-over.', ['utility','component','event-handler'], 'simple'));
const c9 = classNode(p9, 'DragOverlayHelper', [12,57], 'Provides FadeIn and FadeOut animation helpers for drag-and-drop visual overlays.', ['utility','component'], 'simple');
nodes.push(c9); addContains(p9, c9.id);

// DragPackageReader.cs
const p10 = 'src/Wavee.UI.WinUI/DragDrop/DragPackageReader.cs';
nodes.push(fileNode(p10, 'Deserialises a drag data package from a WinUI DataPackageView, resolving track URIs and drag payloads.', ['utility','serialization'], 'simple'));
const c10 = classNode(p10, 'DragPackageReader', [14,30], 'Async reader that extracts drag payload from a DataPackageView.', ['utility','serialization'], 'simple');
nodes.push(c10); addContains(p10, c10.id);

// DragPackageWriter.cs
const p11 = 'src/Wavee.UI.WinUI/DragDrop/DragPackageWriter.cs';
nodes.push(fileNode(p11, 'Serialises drag payload data into a WinUI DataPackage for use as a drag source.', ['utility','serialization'], 'simple'));
const c11 = classNode(p11, 'DragPackageWriter', [15,43], 'Writes drag payload (track URIs and metadata) into a WinUI DataPackage.', ['utility','serialization'], 'simple');
nodes.push(c11); addContains(p11, c11.id);

// DragSourceBehavior.cs
const p12 = 'src/Wavee.UI.WinUI/DragDrop/DragSourceBehavior.cs';
nodes.push(fileNode(p12, 'Attaches WinUI platform drag-start handling to a UIElement, wiring the payload factory and drag-starting lifecycle callbacks.', ['component','event-handler'], 'simple'));
const c12 = classNode(p12, 'DragSourceBehavior', [14,43], 'Static helper that wires DragStarting event on a UIElement with payload and callback delegates.', ['component','event-handler'], 'simple');
nodes.push(c12); addContains(p12, c12.id);

// DragStateService.cs
const p13 = 'src/Wavee.UI.WinUI/DragDrop/DragStateService.cs';
nodes.push(fileNode(p13, 'Singleton service that tracks whether a drag operation is in progress and what payload is being dragged, enabling drop targets to inspect the active drag.', ['service','singleton'], 'simple'));
const c13 = classNode(p13, 'DragStateService', [10,41], 'Provides StartDrag/EndDrag lifecycle management and exposes the current drag payload to drop targets.', ['service','singleton'], 'simple');
nodes.push(c13); addContains(p13, c13.id); addExports(p13, c13.id);

// DropTargetBehavior.cs
const p14 = 'src/Wavee.UI.WinUI/DragDrop/DropTargetBehavior.cs';
nodes.push(fileNode(p14, 'Attaches drop-target event handlers (DragOver/Drop) to a UIElement with configurable kind, target identity resolver, and drop callback.', ['component','event-handler'], 'moderate'));
const c14 = classNode(p14, 'DropTargetBehavior', [18,67], 'Static helper that wires DragOver and Drop events to delegate-based drop handlers with drag-kind filtering.', ['component','event-handler'], 'moderate');
nodes.push(c14); addContains(p14, c14.id);
const f14 = funcNode(p14, 'AttachDropTarget', [20,66], 'Wires DragOver and Drop handlers on a UIElement with kind filtering, resolver delegates, and overlay animations.', ['function','component','event-handler'], 'moderate');
nodes.push(f14); addContains(p14, f14.id); addExports(p14, f14.id);

// LibraryPlaylistMediator.cs
const p15 = 'src/Wavee.UI.WinUI/DragDrop/LibraryPlaylistMediator.cs';
nodes.push(fileNode(p15, 'Mediator that abstracts library drag-and-drop operations: adding tracks, reordering, moving playlists, and resolving track URIs from various sources.', ['service','middleware'], 'moderate'));
const c15 = classNode(p15, 'LibraryPlaylistMediator', [20,89], 'Bridges drag-drop UI actions to library service calls for playlist track mutation, reorder, and rootlist navigation.', ['service','middleware'], 'moderate');
nodes.push(c15); addContains(p15, c15.id); addExports(p15, c15.id);
[
  ['GetPlaylistTrackUrisAsync',[46,54],'Fetches track URIs for a playlist URI for use as a drag-drop source payload.',['function','service','data-model'],'simple'],
  ['GetAlbumTrackUrisAsync',[56,63],'Fetches track URIs for an album URI for use as a drag-drop source payload.',['function','service','data-model'],'simple'],
  ['GetArtistTopTrackUrisAsync',[65,73],'Fetches top track URIs for an artist URI for use as a drag-drop source payload.',['function','service','data-model'],'simple'],
  ['GetLikedSongUrisAsync',[75,82],'Fetches liked song URIs for use as a drag-drop source payload.',['function','service','data-model'],'simple'],
  ['GetShowEpisodeUrisAsync',[84,88],'Fetches episode URIs for a show URI for use as a drag-drop source payload.',['function','service','data-model'],'simple'],
].forEach(([name,lr,sum,tags,cx]) => {
  const fn = funcNode(p15, name, lr, sum, tags, cx);
  nodes.push(fn); addContains(p15, fn.id); addExports(p15, fn.id);
});

// ManualDragAttachment.cs
const p16 = 'src/Wavee.UI.WinUI/DragDrop/ManualDragAttachment.cs';
nodes.push(fileNode(p16, 'Implements pointer-driven manual drag detection for controls that cannot use WinUI built-in drag, synthesising a system drag with custom preview generation.', ['component','event-handler'], 'complex'));
const c16 = classNode(p16, 'ManualDragAttachment', [25,213], 'Attaches pointer event handlers that detect press-and-move and initiate a system DoDragDrop with payload and small preview.', ['component','event-handler'], 'complex');
nodes.push(c16); addContains(p16, c16.id); addExports(p16, c16.id);
[
  ['OnPointerMoved',[73,105],'Detects drag threshold crossing on pointer move and starts a system drag operation with the prepared package.',['function','event-handler','component'],'moderate'],
  ['AttachWithPackageWriter',[116,149],'Attaches full pointer-to-drag wiring including payload writer and optional small preview mode.',['function','component','event-handler'],'moderate'],
  ['ApplySmallDragPreviewAsync',[167,212],'Renders a compact drag preview image from the source element and applies it to the drag args.',['function','component','utility'],'moderate'],
].forEach(([name,lr,sum,tags,cx]) => {
  const fn = funcNode(p16, name, lr, sum, tags, cx);
  nodes.push(fn); addContains(p16, fn.id); addExports(p16, fn.id);
});

// EditorialBackdropRenderer.cs
const p17 = 'src/Wavee.UI.WinUI/Effects/Editorial/EditorialBackdropRenderer.cs';
nodes.push(fileNode(p17, 'GPU-accelerated editorial hero backdrop renderer using Windows Composition that blends a blurred album image with a tinted gradient and noise overlay, with device-lost handling and LRU eviction.', ['service','component','middleware'], 'complex', 'Uses LoadedImageSurface + Windows.UI.Composition brushes with imperative CompositionEffectBrush baking; the NoiseShader D2D1 pixel shader is composed in via ComputeSharp.'));
const c17 = classNode(p17, 'EditorialBackdropRenderer', [31,326], 'Produces and caches CompositionBrush instances for the editorial hero backdrop, managing device-lost recovery and LRU cache eviction.', ['service','component'], 'complex');
nodes.push(c17); addContains(p17, c17.id); addExports(p17, c17.id);
[
  ['GetBrushAsync',[58,123],'Returns a cached or freshly baked CompositionBrush for the given image URI, accent color, and theme.',['function','service','caching'],'complex'],
  ['BakeImperative',[170,269],'Imperatively builds a multi-layer CompositionEffectBrush compositing blur, gradient, tint, and noise for the editorial backdrop.',['function','service','component'],'complex'],
  ['Dispose',[292,305],'Disposes all cached composition brushes and unsubscribes from device-lost events.',['function','service','utility'],'moderate'],
].forEach(([name,lr,sum,tags,cx]) => {
  const fn = funcNode(p17, name, lr, sum, tags, cx);
  nodes.push(fn); addContains(p17, fn.id); addExports(p17, fn.id);
});

// NoiseShader.cs
const p18 = 'src/Wavee.UI.WinUI/Effects/Editorial/NoiseShader.cs';
nodes.push(fileNode(p18, 'ComputeSharp D2D1 pixel shader that generates per-pixel pseudorandom grayscale noise to break up colour banding in the editorial hero backdrop blur.', ['component','middleware'], 'simple', 'Declared as readonly partial struct implementing ID2D1PixelShader; compiled via D2DGeneratedPixelShaderDescriptor at build time (ComputeSharp).'));

// ListExtensions.cs
const p19 = 'src/Wavee.UI.WinUI/Extensions/ListExtensions.cs';
nodes.push(fileNode(p19, 'Extension method providing a Fisher-Yates in-place shuffle for IList<T>.', ['utility'], 'simple'));

// ObservableCollectionExtensions.cs
const p20 = 'src/Wavee.UI.WinUI/Extensions/ObservableCollectionExtensions.cs';
nodes.push(fileNode(p20, 'Extension methods for ObservableCollection<T> providing bulk InsertRange, stable Sort, and ReplaceWith with minimal change notifications.', ['utility','data-model'], 'moderate'));
const c20 = classNode(p20, 'ObservableCollectionExtensions', [12,111], 'Adds InsertRange, Sort (by comparison and key selector), and ReplaceWith helpers to ObservableCollection.', ['utility','data-model'], 'moderate');
nodes.push(c20); addContains(p20, c20.id); addExports(p20, c20.id);
[
  ['InsertRange',[38,47],'Inserts a range of items into an ObservableCollection at a specified index.',['function','utility','data-model'],'simple'],
  ['ReplaceWith',[89,110],'Replaces the contents of an ObservableCollection with a new item sequence with minimal notifications.',['function','utility','data-model'],'moderate'],
].forEach(([name,lr,sum,tags,cx]) => {
  const fn = funcNode(p20, name, lr, sum, tags, cx);
  nodes.push(fn); addContains(p20, fn.id); addExports(p20, fn.id);
});

// AppLifecycleHelper.cs
const p21 = 'src/Wavee.UI.WinUI/Helpers/Application/AppLifecycleHelper.cs';
nodes.push(fileNode(p21, 'Central application bootstrap and teardown helper: builds the IHost DI container, initialises out-of-process audio, wires playback settings, and handles unhandled exceptions.', ['entry-point','service','factory'], 'complex', 'Contains ConfigureHost (760+ lines) and InitializeOutOfProcessAudioAsync (420+ lines) which dominate the file.'));
const c21 = classNode(p21, 'AppLifecycleHelper', [42,1567], 'Static helper class orchestrating full app lifecycle: DI container construction, audio host IPC, settings, and teardown.', ['entry-point','service','factory'], 'complex');
nodes.push(c21); addContains(p21, c21.id); addExports(p21, c21.id);
[
  ['ConfigureHost',[243,1002],'Registers all app services into the IHost DI container including Spotify session, playback, library, navigation, diagnostics, and AI capabilities.',['function','entry-point','factory'],'complex'],
  ['InitializeOutOfProcessAudioAsync',[1016,1439],'Launches or connects to the AudioHost out-of-process audio engine, provisions the PlayPlay runtime pack, configures IPC proxy, and registers all playback services.',['function','service','middleware'],'complex'],
  ['TeardownPlaybackEngineCoreAsync',[1492,1530],'Gracefully tears down the audio pipeline, flushing pending playback state before disconnecting the IPC pipe.',['function','service','event-handler'],'complex'],
  ['InitializeTrackMetadataEnricher',[1537,1566],'Wires the track metadata enricher service that fetches missing metadata (lyrics, video, extended info) on demand.',['function','service','middleware'],'moderate'],
  ['HandleAppUnhandledException',[1532,1535],'Routes unhandled exceptions to the crash logger and optionally shows a notification.',['function','event-handler','utility'],'simple'],
].forEach(([name,lr,sum,tags,cx]) => {
  const fn = funcNode(p21, name, lr, sum, tags, cx);
  nodes.push(fn); addContains(p21, fn.id); addExports(p21, fn.id);
});

// AppNotificationActivationRouter.cs
const p22 = 'src/Wavee.UI.WinUI/Helpers/Application/AppNotificationActivationRouter.cs';
nodes.push(fileNode(p22, 'Routes Windows App SDK toast notification activation arguments to the appropriate in-app navigation target, marshalling back to the UI thread.', ['middleware','event-handler'], 'moderate'));
const c22 = classNode(p22, 'AppNotificationActivationRouter', [24,102], 'Parses toast activation arguments and dispatches navigation actions to the correct page/view model on the UI thread.', ['middleware','event-handler'], 'moderate');
nodes.push(c22); addContains(p22, c22.id); addExports(p22, c22.id);
const f22 = funcNode(p22, 'Route', [38,93], 'Parses the notification activation argument string and executes the corresponding navigation or playback action.', ['function','middleware','event-handler'], 'complex');
nodes.push(f22); addContains(p22, f22.id); addExports(p22, f22.id);

// AppPaths.cs
const p23 = 'src/Wavee.UI.WinUI/Helpers/Application/AppPaths.cs';
nodes.push(fileNode(p23, 'Static class exposing well-known local app data paths (cache, crash log, diagnostics folder) computed once at startup.', ['utility','config'], 'simple'));
const c23 = classNode(p23, 'AppPaths', [6,23], 'Provides static path constants for cache directory, crash log, and diagnostics output directory.', ['utility','config'], 'simple');
nodes.push(c23); addContains(p23, c23.id); addExports(p23, c23.id);

// DeviceIdHelper.cs
const p24 = 'src/Wavee.UI.WinUI/Helpers/Application/DeviceIdHelper.cs';
nodes.push(fileNode(p24, 'Retrieves or generates a stable per-device UUID stored in local app settings, used as the Spotify Connect device identifier.', ['utility','service'], 'simple'));
const c24 = classNode(p24, 'DeviceIdHelper', [6,23], 'Provides GetOrCreateDeviceId which reads or creates a persistent UUID in ApplicationData.LocalSettings.', ['utility','service'], 'simple');
nodes.push(c24); addContains(p24, c24.id); addExports(p24, c24.id);

console.log('Nodes:', nodes.length, 'Edges:', edges.length);
const out = JSON.stringify({ nodes, edges }, null, 2);
fs.writeFileSync('C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-41.json', out);
console.log('Written batch-41.json');
