const fs = require('fs');

const nodes = [];
const edges = [];

function fileNode(path, summary, tags, complexity, languageNotes) {
  const name = path.split('/').pop();
  const n = {id: 'file:' + path, type: 'file', name, filePath: path, summary, tags, complexity};
  if (languageNotes) n.languageNotes = languageNotes;
  return n;
}
function classNode(path, name, start, end, summary, tags, complexity) {
  return {id: 'class:' + path + ':' + name, type: 'class', name, filePath: path, lineRange: [start, end], summary, tags, complexity};
}
function funcNode(path, name, start, end, summary, tags, complexity) {
  return {id: 'function:' + path + ':' + name, type: 'function', name, filePath: path, lineRange: [start, end], summary, tags, complexity};
}
function contains(path, subId) {
  return {source: 'file:' + path, target: subId, type: 'contains', direction: 'forward', weight: 1.0};
}
function exports_(path, subId) {
  return {source: 'file:' + path, target: subId, type: 'exports', direction: 'forward', weight: 0.8};
}

// ---- IVideoSurfaceProvider.cs ----
const f1 = 'src/Wavee.UI.WinUI/Services/IVideoSurfaceProvider.cs';
nodes.push(fileNode(f1, 'Defines the IVideoSurfaceProvider interface exposing GPU composition surface, buffering state, and surface-change observables used by the local video playback pipeline.', ['service', 'type-definition', 'video', 'interface'], 'moderate'));
const c1 = classNode(f1, 'IVideoSurfaceProvider', 31, 73, 'Interface contract for providing a WinUI composition video surface, its loading/buffering state, and an observable stream of surface changes.', ['interface', 'video', 'service'], 'moderate');
nodes.push(c1); edges.push(contains(f1, c1.id)); edges.push(exports_(f1, c1.id));

// ---- LibraryRecentsService.cs ----
const f2 = 'src/Wavee.UI.WinUI/Services/LibraryRecentsService.cs';
nodes.push(fileNode(f2, 'Caches and serves recently-played album and artist data fetched from Spotify SpClient, with TTL-based invalidation triggered by playback context changes.', ['service', 'library', 'cache', 'recents'], 'moderate'));
const c2 = classNode(f2, 'LibraryRecentsService', 19, 167, 'TTL cache for recently played albums and artists, fetching from SpClient and broadcasting changes via DispatcherQueue-marshalled events.', ['service', 'cache', 'library'], 'moderate');
nodes.push(c2); edges.push(contains(f2, c2.id)); edges.push(exports_(f2, c2.id));
const c2f1 = funcNode(f2, 'GetOrFetchAsync', 83, 142, 'Generic TTL-gated fetch that acquires a per-type SemaphoreSlim lock, calls Spotify SpClient if stale, and fires change notification.', ['cache', 'async', 'utility'], 'complex');
nodes.push(c2f1); edges.push(contains(f2, c2f1.id));

// ---- LocalEpisodeChapterScanner.cs ----
const f3 = 'src/Wavee.UI.WinUI/Services/LocalEpisodeChapterScanner.cs';
nodes.push(fileNode(f3, 'Scans local video files for embedded chapter cues using WinRT media APIs and caches the resulting EpisodeChapter list per track URI.', ['service', 'local-media', 'scanning', 'cache'], 'moderate'));
const c3 = classNode(f3, 'LocalEpisodeChapterScanner', 26, 132, 'Extracts and caches chapter cue points from local episode files by reading WinRT timed metadata tracks and decoding UTF-8 labels.', ['local-media', 'scanning', 'cache'], 'moderate');
nodes.push(c3); edges.push(contains(f3, c3.id)); edges.push(exports_(f3, c3.id));
const c3f1 = funcNode(f3, 'ReadChapters', 63, 95, 'Reads all timed-text cue tracks from a local media playback item and converts them to EpisodeChapter value objects.', ['local-media', 'parsing'], 'moderate');
nodes.push(c3f1); edges.push(contains(f3, c3f1.id));
const c3f2 = funcNode(f3, 'ExtractCueLabel', 97, 131, 'Decodes a WinRT IMediaCue label from UTF-8 bytes via DataReader, falling back through encoding strategies.', ['local-media', 'encoding', 'utility'], 'moderate');
nodes.push(c3f2); edges.push(contains(f3, c3f2.id));

// ---- LocalItemContextMenuPresenter.cs ----
const f4 = 'src/Wavee.UI.WinUI/Services/LocalItemContextMenuPresenter.cs';
nodes.push(fileNode(f4, 'Builds and shows the context flyout for local media items, wiring play, like, link/unlink Spotify track, edit metadata, and delete actions.', ['service', 'context-menu', 'local-media', 'ui'], 'complex'));
const c4 = classNode(f4, 'LocalItemContextMenuPresenter', 29, 292, 'Static presenter that constructs context menus for local library items and dispatches actions to ILocalLibraryFacade and music-video services.', ['context-menu', 'local-media', 'service'], 'complex');
nodes.push(c4); edges.push(contains(f4, c4.id)); edges.push(exports_(f4, c4.id));
const c4f1 = funcNode(f4, 'Show', 31, 127, 'Main entry: builds the action set for a local track and shows the context menu flyout anchored to the pointer position.', ['context-menu', 'entry-point', 'ui'], 'complex');
nodes.push(c4f1); edges.push(contains(f4, c4f1.id));
const c4f2 = funcNode(f4, 'ShowLinkSpotifyTrackFlyout', 159, 185, 'Shows a flyout containing a LinkSpotifyTrackFlyout picker so the user can associate a local video with a Spotify track URI.', ['ui', 'flyout', 'local-media'], 'moderate');
nodes.push(c4f2); edges.push(contains(f4, c4f2.id));
const c4f3 = funcNode(f4, 'TryNormalizeSpotifyTrackUri', 242, 279, 'Parses and normalizes raw user input (Spotify URI, open.spotify.com URL, or base-62 ID) into a canonical spotify:track:<id> URI.', ['utility', 'parsing', 'validation'], 'moderate');
nodes.push(c4f3); edges.push(contains(f4, c4f3.id));

// ---- LocalLibraryFacade.cs ----
const f5 = 'src/Wavee.UI.WinUI/Services/LocalLibraryFacade.cs';
nodes.push(fileNode(f5, 'Facade aggregating local library reads (shows, movies, music videos, collections, liked tracks) and writes (likes, kinds, metadata, artwork, play records) into a unified reactive ILocalLibraryFacade.', ['service', 'facade', 'local-media', 'library'], 'complex'));
const c5 = classNode(f5, 'LocalLibraryFacade', 34, 317, 'Aggregates ILocalLibraryService, ILocalLikesService, ILocalEnrichmentService, and ILocalGroupService into a composable facade emitting LocalLibraryChange notifications.', ['facade', 'service', 'local-media'], 'complex');
nodes.push(c5); edges.push(contains(f5, c5.id)); edges.push(exports_(f5, c5.id));
const c5f1 = funcNode(f5, 'EnrichLinkedMusicVideoFromSpotifyAsync', 222, 297, 'Fetches Spotify track metadata for a linked local video, patches title/artist/year, downloads album cover, and saves it as an artwork override.', ['enrichment', 'spotify', 'async'], 'complex');
nodes.push(c5f1); edges.push(contains(f5, c5f1.id));

// ---- LocalMediaPlayer.cs ----
const f6 = 'src/Wavee.UI.WinUI/Services/LocalMediaPlayer.cs';
nodes.push(fileNode(f6, 'WinRT MediaPlayer wrapper for local file playback, managing subtitle attachment, state publication via Rx subjects, surface change notifications, and UI-thread marshalling.', ['service', 'playback', 'local-media', 'winrt'], 'complex'));
const c6 = classNode(f6, 'LocalMediaPlayer', 30, 599, 'Full-lifecycle local media player on WinRT Windows.Media.Playback.MediaPlayer, publishing playback state and surface changes as IObservable streams.', ['playback', 'local-media', 'service'], 'complex');
nodes.push(c6); edges.push(contains(f6, c6.id)); edges.push(exports_(f6, c6.id));
const c6f1 = funcNode(f6, 'PlayFileAsync', 112, 170, 'Opens a local file URI in the MediaPlayer, configures start position, starts the position-tick timer, and fires surface/metadata change notifications.', ['playback', 'async', 'entry-point'], 'complex');
nodes.push(c6f1); edges.push(contains(f6, c6f1.id));
const c6f2 = funcNode(f6, 'AddExternalSubtitleAsync', 202, 280, 'Persists a dropped subtitle file to the library and attaches it as a TimedTextSource to the current MediaPlaybackItem on the UI thread.', ['subtitles', 'local-media', 'async'], 'complex');
nodes.push(c6f2); edges.push(contains(f6, c6f2.id));
const c6f3 = funcNode(f6, 'AttachPersistedSubtitlesAsync', 334, 375, 'Queries the library for previously saved subtitle entries and re-attaches each as an external TimedTextSource.', ['subtitles', 'local-media', 'async'], 'complex');
nodes.push(c6f3); edges.push(contains(f6, c6f3.id));
const c6f4 = funcNode(f6, 'PublishStateFromSession', 516, 554, 'Reads current MediaPlayer session state and emits a LocalPlaybackState snapshot on the state Rx subject.', ['playback', 'reactive', 'state'], 'moderate');
nodes.push(c6f4); edges.push(contains(f6, c6f4.id));

// ---- LocalPlaybackLauncher.cs ----
const f7 = 'src/Wavee.UI.WinUI/Services/LocalPlaybackLauncher.cs';
nodes.push(fileNode(f7, 'Static helper that fires-and-forgets local track playback via IPlaybackCommandExecutor, building a PlaybackContextInfo for single-track or queue scenarios.', ['service', 'playback', 'local-media', 'utility'], 'moderate'));
const c7 = classNode(f7, 'LocalPlaybackLauncher', 18, 78, 'Provides static PlayOne and PlayQueue helpers resolving IPlaybackCommandExecutor from IoC and dispatching PlayTracksAsync on a background task.', ['playback', 'local-media', 'utility'], 'moderate');
nodes.push(c7); edges.push(contains(f7, c7.id)); edges.push(exports_(f7, c7.id));
const c7f1 = funcNode(f7, 'PlayQueue', 28, 77, 'Resolves the playback executor from IoC, builds a PlaybackContextInfo, and asynchronously calls PlayTracksAsync with URI list and start index.', ['playback', 'local-media', 'async'], 'moderate');
nodes.push(c7f1); edges.push(contains(f7, c7f1.id));

// ---- LocalPlaybackProgressTracker.cs ----
const f8 = 'src/Wavee.UI.WinUI/Services/LocalPlaybackProgressTracker.cs';
nodes.push(fileNode(f8, 'Observes LocalMediaPlayer state changes to periodically write playback position to the library and fire watched/play-record events at threshold milestones.', ['service', 'playback', 'progress-tracking', 'local-media'], 'moderate'));
const c8 = classNode(f8, 'LocalPlaybackProgressTracker', 22, 114, 'Subscribes to LocalMediaPlayer StateChanges and TrackFinished observables to throttle position writes and record completed plays against ILocalLibraryFacade.', ['playback', 'progress-tracking', 'reactive'], 'moderate');
nodes.push(c8); edges.push(contains(f8, c8.id)); edges.push(exports_(f8, c8.id));
const c8f1 = funcNode(f8, 'OnState', 50, 86, 'Throttles SetLastPositionAsync writes by WriteEveryMs and fires MarkWatchedAsync once the WatchedThreshold fraction is reached.', ['progress-tracking', 'event-handler', 'playback'], 'moderate');
nodes.push(c8f1); edges.push(contains(f8, c8f1.id));

// ---- LocalShowEpisodeQueue.cs ----
const f9 = 'src/Wavee.UI.WinUI/Services/LocalShowEpisodeQueue.cs';
nodes.push(fileNode(f9, 'Utility for building an ordered playable episode queue from local show seasons, resolving the first unwatched episode, or the next episode after the current one.', ['utility', 'local-media', 'queue', 'episodes'], 'moderate'));
const c9 = classNode(f9, 'LocalShowEpisodeQueue', 13, 64, 'Static helpers for traversing LocalSeasonViewModel trees to produce ordered playable URI lists, first-unwatched resolution, and next-episode lookups.', ['local-media', 'queue', 'utility'], 'moderate');
nodes.push(c9); edges.push(contains(f9, c9.id)); edges.push(exports_(f9, c9.id));

// ---- LyricsAiEvidenceParser.cs ----
const f10 = 'src/Wavee.UI.WinUI/Services/LyricsAi/LyricsAiEvidenceParser.cs';
nodes.push(fileNode(f10, 'Parses structured JSON evidence from the Phi Silica LyricsAI response, extracting text segments and citations and reconstructing annotated explanation text.', ['service', 'ai', 'lyrics', 'parsing'], 'complex'));
const c10 = classNode(f10, 'LyricsAiEvidenceParser', 8, 366, 'Holds the evidence JSON schema and provides static methods to parse AI-returned lyric-meaning JSON into LyricsAiTextSegment and LyricsAiCitation lists.', ['ai', 'lyrics', 'parsing'], 'complex');
nodes.push(c10); edges.push(contains(f10, c10.id));
const c10f1 = funcNode(f10, 'TryParseLyricsMeaningEvidence', 87, 219, 'Deserializes the AI evidence JSON, validates segment structure and citation indices, and out-params the segment/citation arrays.', ['parsing', 'ai', 'validation'], 'complex');
nodes.push(c10f1); edges.push(contains(f10, c10f1.id));
const c10f2 = funcNode(f10, 'ReconstructTextFromSegments', 321, 365, 'Reassembles the annotated explanation paragraph from a mixed list of plain-text and citation segments.', ['parsing', 'text-processing'], 'moderate');
nodes.push(c10f2); edges.push(contains(f10, c10f2.id));

// ---- LyricsAiOutputNormalizer.cs ----
const f11 = 'src/Wavee.UI.WinUI/Services/LyricsAi/LyricsAiOutputNormalizer.cs';
nodes.push(fileNode(f11, 'Cleans raw Phi Silica LyricsAI text output by stripping evidence reference lines, list prefixes, and normalizing whitespace.', ['utility', 'ai', 'lyrics', 'text-processing'], 'moderate'));
const c11 = classNode(f11, 'LyricsAiOutputNormalizer', 6, 89, 'Static normalizer that removes AI evidence-marker lines and list-item prefixes from raw model output to produce human-readable lyrics analysis.', ['ai', 'lyrics', 'utility'], 'moderate');
nodes.push(c11); edges.push(contains(f11, c11.id));
const c11f1 = funcNode(f11, 'NormalizeLyricsMeaningOutput', 8, 42, 'Applies a normalization pipeline (strip evidence lines, strip list prefixes, collapse whitespace) to raw AI text.', ['text-processing', 'ai', 'utility'], 'moderate');
nodes.push(c11f1); edges.push(contains(f11, c11f1.id));

// ---- LyricsAiPrompts.cs ----
const f12 = 'src/Wavee.UI.WinUI/Services/LyricsAi/LyricsAiPrompts.cs';
nodes.push(fileNode(f12, 'Builds structured on-device AI prompts for Phi Silica lyric-meaning and song-summary requests, embedding numbered lyrics and track context.', ['service', 'ai', 'lyrics', 'prompt-engineering'], 'moderate'));
const c12 = classNode(f12, 'LyricsAiPrompts', 6, 127, 'Provides prompt-construction helpers for the LyricsAI pipeline, building numbered lyric context blocks and metadata fragments for primary and fallback calls.', ['ai', 'lyrics', 'prompt-engineering'], 'moderate');
nodes.push(c12); edges.push(contains(f12, c12.id));
const c12f1 = funcNode(f12, 'BuildLyricsMeaningPrompt', 21, 42, 'Assembles the primary LyricsAI prompt combining system instructions, numbered lyric context, and track metadata.', ['ai', 'lyrics', 'prompt-engineering'], 'moderate');
nodes.push(c12f1); edges.push(contains(f12, c12f1.id));

// ---- LyricsAiResult.cs ----
const f13 = 'src/Wavee.UI.WinUI/Services/LyricsAi/LyricsAiResult.cs';
nodes.push(fileNode(f13, 'Defines the LyricsAiResult discriminated union and supporting record types (LyricsAiTextSegment, LyricsAiCitation) returned by the LyricsAI pipeline.', ['data-model', 'ai', 'lyrics', 'type-definition'], 'moderate'));

// ---- LyricsAiService.cs ----
const f14 = 'src/Wavee.UI.WinUI/Services/LyricsAi/LyricsAiService.cs';
nodes.push(fileNode(f14, 'On-device AI service using Phi Silica LanguageModel to explain selected lyric lines or summarize a song, with in-memory result caching per track URI.', ['service', 'ai', 'lyrics', 'cache'], 'moderate'));
const c14 = classNode(f14, 'LyricsAiService', 31, 163, 'Orchestrates Phi Silica prompt dispatch, result caching, and evidence parsing for lyric-meaning and song-summary features.', ['ai', 'lyrics', 'service'], 'moderate');
nodes.push(c14); edges.push(contains(f14, c14.id)); edges.push(exports_(f14, c14.id));
const c14f1 = funcNode(f14, 'GenerateLyricsMeaningCoreAsync', 121, 159, 'Calls Phi Silica LanguageModel with the primary prompt then falls back to a simpler prompt on parse failure, returning a normalized LyricsAiResult.', ['ai', 'lyrics', 'async'], 'complex');
nodes.push(c14f1); edges.push(contains(f14, c14f1.id));

// ---- LyricsCacheModels.cs ----
const f15 = 'src/Wavee.UI.WinUI/Services/LyricsCacheModels.cs';
nodes.push(fileNode(f15, 'AOT-safe JSON serialization models and converters for the lyrics disk cache, mapping between LyricsResult domain objects and flat DTOs.', ['data-model', 'cache', 'lyrics', 'serialization'], 'moderate'));
const c15a = classNode(f15, 'LyricsCacheJsonContext', 34, 36, 'Source-generated JsonSerializerContext for AOT-compatible lyrics cache serialization.', ['serialization', 'cache'], 'simple');
nodes.push(c15a); edges.push(contains(f15, c15a.id));
const c15b = classNode(f15, 'LyricsCacheConverter', 38, 103, 'Custom JsonConverter mapping LyricsResult domain objects to/from disk-cache DTOs, handling line arrays and provider metadata.', ['serialization', 'cache', 'lyrics'], 'moderate');
nodes.push(c15b); edges.push(contains(f15, c15b.id));

// ---- LyricsSearchDiagnostics.cs ----
const f16 = 'src/Wavee.UI.WinUI/Services/LyricsSearchDiagnostics.cs';
nodes.push(fileNode(f16, 'Simple data records holding lyrics provider search diagnostics (per-provider timing and hit/miss results) for debug display.', ['data-model', 'lyrics', 'diagnostics'], 'simple'));

// ---- LyricsService.cs ----
const f17 = 'src/Wavee.UI.WinUI/Services/LyricsService.cs';
nodes.push(fileNode(f17, 'Central lyrics service orchestrating multi-provider search (Spotify, QQMusic, Kugou, Netease, LrcLib, Musixmatch, AMLL TTML DB) with disk caching, scoring, and episode-transcript support.', ['service', 'lyrics', 'cache', 'multi-provider'], 'complex'));
const c17 = classNode(f17, 'LyricsService', 33, 1206, 'Implements ILyricsService querying all configured providers in parallel, ranking results by fuzzy title/artist match and Spotify-anchor scoring, caching to disk, and surfacing diagnostics.', ['lyrics', 'service', 'cache'], 'complex');
nodes.push(c17); edges.push(contains(f17, c17.id)); edges.push(exports_(f17, c17.id));
const c17f1 = funcNode(f17, 'GetLyricsForTrackAsync', 76, 297, 'Main search pipeline: checks disk cache, queries all providers in parallel, scores results against Spotify anchors, picks the best match, and writes to disk cache.', ['lyrics', 'cache', 'async'], 'complex');
nodes.push(c17f1); edges.push(contains(f17, c17f1.id));
const c17f2 = funcNode(f17, 'ScoreAgainstSpotify', 303, 350, 'Computes a normalized similarity score between a provider result and the canonical Spotify line list using fuzzy normalization and anchor matching.', ['lyrics', 'scoring', 'utility'], 'complex');
nodes.push(c17f2); edges.push(contains(f17, c17f2.id));
const c17f3 = funcNode(f17, 'GetEpisodeTranscriptInternalAsync', 417, 526, 'Fetches podcast episode transcripts from Spotify SpClient, converts VTT cue times to LRC timestamps, and returns a structured LyricsResult.', ['lyrics', 'podcast', 'async'], 'complex');
nodes.push(c17f3); edges.push(contains(f17, c17f3.id));
const c17f4 = funcNode(f17, 'SearchMusixmatchAsync', 787, 864, 'Queries the Musixmatch provider API, normalizes results, and returns a scored LyricsResult candidate.', ['lyrics', 'provider', 'async'], 'complex');
nodes.push(c17f4); edges.push(contains(f17, c17f4.id));
const c17f5 = funcNode(f17, 'SearchLrcLibAsync', 683, 762, 'Searches LrcLib API for synchronized and unsynchronized lyrics, applying anchor matching and result normalization.', ['lyrics', 'provider', 'async'], 'complex');
nodes.push(c17f5); edges.push(contains(f17, c17f5.id));

// ---- MediaFoundationEmbeddedTrackProber.cs ----
const f18 = 'src/Wavee.UI.WinUI/Services/MediaFoundationEmbeddedTrackProber.cs';
nodes.push(fileNode(f18, 'Probes local media files via Windows Media Foundation to extract embedded tag metadata (title, artist, album, duration, cover art) without full file open.', ['service', 'local-media', 'metadata', 'media-foundation'], 'moderate'));
const c18 = classNode(f18, 'MediaFoundationEmbeddedTrackProber', 25, 123, 'Wraps Windows MF source reader to extract embedded tag data from local audio/video files asynchronously.', ['local-media', 'metadata', 'service'], 'moderate');
nodes.push(c18); edges.push(contains(f18, c18.id)); edges.push(exports_(f18, c18.id));
const c18f1 = funcNode(f18, 'ProbeAsync', 47, 119, 'Asynchronously reads embedded metadata properties from the MF source reader and maps them to a LocalTrackMetadata record.', ['local-media', 'metadata', 'async'], 'complex');
nodes.push(c18f1); edges.push(contains(f18, c18f1.id));

// ---- MediaOverrideService.cs ----
const f19 = 'src/Wavee.UI.WinUI/Services/MediaOverrideService.cs';
nodes.push(fileNode(f19, 'Service for managing track-canvas URL overrides: resolving active canvas (auto-detected, pending, manual, or upstream), accepting/rejecting pending updates, and importing user-supplied images.', ['service', 'media-override', 'local-media', 'canvas'], 'complex'));
const c19a = classNode(f19, 'IMediaOverrideService', 13, 41, 'Interface contract for the track canvas override service exposing resolve, accept/reject, set-manual, import-file, and reset operations.', ['interface', 'service', 'canvas'], 'moderate');
nodes.push(c19a); edges.push(contains(f19, c19a.id)); edges.push(exports_(f19, c19a.id));
const c19b = classNode(f19, 'MediaOverrideService', 53, 409, 'Concrete implementation managing per-track canvas override state, persisting managed images to disk, and resolving the best available URL through a priority chain.', ['service', 'canvas', 'local-media'], 'complex');
nodes.push(c19b); edges.push(contains(f19, c19b.id)); edges.push(exports_(f19, c19b.id));
const c19f1 = funcNode(f19, 'ResolveTrackCanvasAsync', 70, 156, 'Priority-ordered resolution: returns pending-preview if present, else managed-file URL, else manual URL, else upstream default.', ['canvas', 'service', 'async'], 'complex');
nodes.push(c19f1); edges.push(contains(f19, c19f1.id));

// ---- MemoryBudgetService.cs ----
const f20 = 'src/Wavee.UI.WinUI/Services/MemoryBudgetService.cs';
nodes.push(fileNode(f20, 'Monitors app memory against configurable working-set thresholds, orchestrating progressive cache eviction (image caches, nav-caches, warm caches, GC compaction) to stay within budget.', ['service', 'memory-management', 'performance', 'cache'], 'complex'));
const c20 = classNode(f20, 'MemoryBudgetService', 26, 510, 'Periodic memory watcher hooking WinRT AppMemoryUsage events and running a tiered eviction loop (image clear, stale cache, nav-surface release, warm cache, GC trim) with per-page attribution logging.', ['memory-management', 'service', 'performance'], 'complex');
nodes.push(c20); edges.push(contains(f20, c20.id)); edges.push(exports_(f20, c20.id));
const c20f1 = funcNode(f20, 'CheckAsync', 173, 236, 'Measures current working set, determines overage severity, and dispatches the appropriate eviction tier.', ['memory-management', 'async', 'performance'], 'complex');
nodes.push(c20f1); edges.push(contains(f20, c20f1.id));
const c20f2 = funcNode(f20, 'LogMemoryAttributionAsync', 291, 322, 'Iterates live nav-cached frames and accumulates per-page-type image surface memory attribution for debug logging.', ['memory-management', 'diagnostics', 'async'], 'moderate');
nodes.push(c20f2); edges.push(contains(f20, c20f2.id));

// ---- MemoryReleaseHelper.cs ----
const f21 = 'src/Wavee.UI.WinUI/Services/MemoryReleaseHelper.cs';
nodes.push(fileNode(f21, 'Low-level helper for releasing process working set via kernel SetProcessWorkingSetSizeEx, with safe current-size measurement and debounce logic.', ['utility', 'memory-management', 'performance', 'native'], 'moderate'));
const c21 = classNode(f21, 'MemoryReleaseHelper', 28, 120, 'P/Invoke wrapper around SetProcessWorkingSetSizeEx providing throttled and immediate working-set trim with safe current-size fallback.', ['memory-management', 'native', 'utility'], 'moderate');
nodes.push(c21); edges.push(contains(f21, c21.id)); edges.push(exports_(f21, c21.id));
const c21f1 = funcNode(f21, 'ReleaseWorkingSetNow', 46, 78, 'Reads current process working set via NtQueryInformationProcess, computes a safe trim target, and calls SetProcessWorkingSetSizeEx to release pages.', ['memory-management', 'native', 'performance'], 'complex');
nodes.push(c21f1); edges.push(contains(f21, c21f1.id));

// ---- IMusicVideoCatalogCache.cs ----
const f22 = 'src/Wavee.UI.WinUI/Services/MusicVideos/IMusicVideoCatalogCache.cs';
nodes.push(fileNode(f22, 'Interface for a per-track music video catalog cache storing video metadata, Spotify associations, and TTL-based expiry.', ['service', 'interface', 'music-video', 'cache'], 'moderate'));
const c22 = classNode(f22, 'IMusicVideoCatalogCache', 21, 73, 'Contract for reading and writing cached music video metadata entries keyed by Spotify track URI.', ['interface', 'music-video', 'cache'], 'moderate');
nodes.push(c22); edges.push(contains(f22, c22.id)); edges.push(exports_(f22, c22.id));

// ---- IMusicVideoDiscoveryService.cs ----
const f23 = 'src/Wavee.UI.WinUI/Services/MusicVideos/IMusicVideoDiscoveryService.cs';
nodes.push(fileNode(f23, 'Interface for the music video background discovery service that matches local video files to Spotify tracks.', ['service', 'interface', 'music-video', 'discovery'], 'simple'));
const c23 = classNode(f23, 'IMusicVideoDiscoveryService', 18, 44, 'Contract for initiating and cancelling background music-video-to-Spotify-track matching discovery passes.', ['interface', 'music-video', 'discovery'], 'simple');
nodes.push(c23); edges.push(contains(f23, c23.id)); edges.push(exports_(f23, c23.id));

// ---- IMusicVideoMetadataService.cs ----
const f24 = 'src/Wavee.UI.WinUI/Services/MusicVideos/IMusicVideoMetadataService.cs';
nodes.push(fileNode(f24, 'Interface for querying, updating, and forgetting Spotify-enriched metadata associations for local music video files.', ['service', 'interface', 'music-video', 'metadata'], 'simple'));
const c24 = classNode(f24, 'IMusicVideoMetadataService', 8, 58, 'Contract exposing GetAssociationAsync, UpdateVideoAssociationAsync, and ForgetVideoAssociation for managing music video Spotify track links.', ['interface', 'music-video', 'metadata'], 'simple');
nodes.push(c24); edges.push(contains(f24, c24.id)); edges.push(exports_(f24, c24.id));

// ---- MusicVideoAssociationParser.cs ----
const f25 = 'src/Wavee.UI.WinUI/Services/MusicVideos/MusicVideoAssociationParser.cs';
nodes.push(fileNode(f25, 'Static parser for extracting video and audio Spotify track URI associations from local file sidecar metadata, handling plain-list and structured formats.', ['utility', 'music-video', 'parsing', 'local-media'], 'moderate'));
const c25 = classNode(f25, 'MusicVideoAssociationParser', 9, 85, 'Parses sidecar metadata fields to determine whether a local file has a Spotify track video/audio association and extracts the associated URI.', ['music-video', 'parsing', 'utility'], 'moderate');
nodes.push(c25); edges.push(contains(f25, c25.id)); edges.push(exports_(f25, c25.id));
const c25f1 = funcNode(f25, 'TryReadPlainListAssociationUri', 36, 74, 'Searches a semicolon-delimited list of URI tokens in the sidecar field and returns the first valid Spotify track URI entry.', ['music-video', 'parsing', 'utility'], 'moderate');
nodes.push(c25f1); edges.push(contains(f25, c25f1.id));

const output = {nodes, edges};
console.log('nodes:', nodes.length, 'edges:', edges.length);
fs.writeFileSync('C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-45.json', JSON.stringify(output, null, 2), {encoding: 'utf8'});
console.log('Written batch-45.json');
