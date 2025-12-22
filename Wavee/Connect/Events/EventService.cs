using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http;
using Wavee.Core.Utilities;

namespace Wavee.Connect.Events;

/// <summary>
/// Service for sending playback events to Spotify's event-service.
/// Events are sent asynchronously in the background to not block playback.
/// </summary>
/// <remarks>
/// Based on librespot-java's EventService.
/// Events are sent to hm://event-service/v1/events via Mercury,
/// or via HTTP at spclient.wg.spotify.com/event-service/v1/events.
/// </remarks>
public sealed class EventService : IAsyncDisposable
{
    private readonly SpClient _spClient;
    private readonly ILogger? _logger;
    private readonly AsyncWorker<EventBuilder> _asyncWorker;
    private readonly Subject<IPlaybackEvent> _eventSubject = new();
    private bool _disposed;

    /// <summary>
    /// Observable stream of all playback events.
    /// Local subscribers (like LibraryPlayRecorder) can use this to react to playback events.
    /// </summary>
    public IObservable<IPlaybackEvent> Events => _eventSubject.AsObservable();

    /// <summary>
    /// Creates a new EventService.
    /// </summary>
    /// <param name="spClient">SpClient for HTTP requests.</param>
    /// <param name="logger">Optional logger.</param>
    public EventService(SpClient spClient, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(spClient);

        _spClient = spClient;
        _logger = logger;

        _asyncWorker = new AsyncWorker<EventBuilder>(
            "EventService",
            SendEventInternalAsync,
            logger);
    }

    /// <summary>
    /// Sends a playback event to Spotify.
    /// The event is queued and sent asynchronously.
    /// Also publishes to local subscribers via the Events observable.
    /// </summary>
    /// <param name="playbackEvent">Event to send.</param>
    public void SendEvent(IPlaybackEvent playbackEvent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Publish to local subscribers first (for library recording, etc.)
        _eventSubject.OnNext(playbackEvent);

        try
        {
            var builder = playbackEvent.Build();
            if (!_asyncWorker.TrySubmit(builder))
            {
                _logger?.LogWarning("Event queue full, dropping event: {EventType}", playbackEvent.GetType().Name);
            }
            else
            {
                _logger?.LogDebug("Event queued: {EventType}", playbackEvent.GetType().Name);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to build event: {EventType}", playbackEvent.GetType().Name);
        }
    }

    /// <summary>
    /// Sends an event builder directly.
    /// </summary>
    public void SendEvent(EventBuilder builder)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _asyncWorker.TrySubmit(builder);
    }

    private async ValueTask SendEventInternalAsync(EventBuilder builder)
    {
        try
        {
            var body = builder.ToArray();
            var debugString = EventBuilder.ToDebugString(body);

            _logger?.LogDebug("Sending event: {Event}", debugString);

            await _spClient.PostEventAsync(body);

            _logger?.LogDebug("Event sent successfully");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to send event: {Event}", builder);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Complete and dispose the event subject
        _eventSubject.OnCompleted();
        _eventSubject.Dispose();

        // Wait for pending events to be sent (with timeout)
        try
        {
            await _asyncWorker.DisposeAsync();
            _logger?.LogDebug("EventService disposed, all pending events sent");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error during EventService disposal");
        }
    }
}
