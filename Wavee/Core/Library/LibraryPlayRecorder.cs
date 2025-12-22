using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using Wavee.Connect.Events;
using Wavee.Core.Storage;

namespace Wavee.Core.Library;

/// <summary>
/// Records plays to the library by subscribing to EventService events.
/// Automatically captures all track transitions and stores them in the library.
/// </summary>
public sealed class LibraryPlayRecorder : IAsyncDisposable
{
    private readonly ILibraryService _libraryService;
    private readonly ICacheService? _cacheService;
    private readonly ILogger? _logger;
    private readonly IDisposable _subscription;
    private bool _disposed;

    /// <summary>
    /// Creates a new LibraryPlayRecorder.
    /// </summary>
    /// <param name="eventService">EventService to subscribe to for playback events.</param>
    /// <param name="libraryService">Library service to record plays to.</param>
    /// <param name="cacheService">Optional cache service to look up track metadata.</param>
    /// <param name="logger">Optional logger.</param>
    public LibraryPlayRecorder(
        EventService eventService,
        ILibraryService libraryService,
        ICacheService? cacheService = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(eventService);
        ArgumentNullException.ThrowIfNull(libraryService);

        _libraryService = libraryService;
        _cacheService = cacheService;
        _logger = logger;

        // Subscribe to track transitions
        _subscription = eventService.Events
            .OfType<TrackTransitionEvent>()
            .Subscribe(OnTrackTransition);

        _logger?.LogInformation("LibraryPlayRecorder started - listening for track transitions");
    }

    private async void OnTrackTransition(TrackTransitionEvent evt)
    {
        if (_disposed)
            return;

        try
        {
            var metrics = evt.Metrics;

            // Calculate play duration from intervals
            var durationPlayedMs = metrics.LastValue - metrics.FirstValue;
            if (durationPlayedMs <= 0)
            {
                _logger?.LogDebug("Skipping play recording: no duration played");
                return;
            }

            // Determine if track was completed (TrackDone reason means played to end)
            var completed = metrics.ReasonEnd == PlaybackReason.TrackDone;

            // Build track URI from track ID (metrics stores just the ID part)
            var trackUri = $"spotify:track:{metrics.TrackId}";

            // Try to get track metadata from cache
            string title = "Unknown Track";
            string? artist = null;
            string? album = null;
            long totalDurationMs = metrics.Player?.Duration ?? 0;
            string? imageUrl = null;

            if (_cacheService != null)
            {
                var cached = await _cacheService.GetTrackAsync(trackUri);
                if (cached != null)
                {
                    title = cached.Title ?? title;
                    artist = cached.Artist;
                    album = cached.Album;
                    totalDurationMs = cached.DurationMs ?? totalDurationMs;
                    imageUrl = cached.ImageUrl;
                    _logger?.LogDebug("Found cached metadata for {TrackUri}: {Title} by {Artist}",
                        trackUri, title, artist);
                }
                else
                {
                    _logger?.LogDebug("No cached metadata for {TrackUri}, using minimal data", trackUri);
                }
            }

            // Create library item from track data
            var item = LibraryItem.FromSpotifyTrack(
                uri: trackUri,
                title: title,
                artist: artist,
                album: album,
                durationMs: totalDurationMs,
                imageUrl: imageUrl);

            // Record the play
            await _libraryService.RecordPlayAsync(
                item,
                durationPlayedMs,
                completed,
                metrics.ContextUri);

            _logger?.LogInformation(
                "Recorded play: {Title} by {Artist} ({Duration}ms, completed={Completed})",
                title, artist ?? "Unknown", durationPlayedMs, completed);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to record play for track transition");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        _subscription.Dispose();
        _logger?.LogDebug("LibraryPlayRecorder disposed");

        return ValueTask.CompletedTask;
    }
}
