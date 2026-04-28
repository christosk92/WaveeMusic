using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http;
using Wavee.Core.Utilities;
using Wavee.Protocol.EventSender;

namespace Wavee.Connect.Events;

/// <summary>
/// Posts playback events to Spotify's gabo-receiver-service. Each
/// <see cref="IPlaybackEvent"/> is wrapped in an <see cref="EventEnvelope"/>
/// (with the standard context fragments), batched into a
/// <see cref="PublishEventsRequest"/>, and POSTed via
/// <see cref="SpClient.PostGaboEventAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the surface that drives Spotify's Recently Played and play counts.
/// The legacy librespot-java event-service path (Mercury <c>hm://event-service/v1/events</c>
/// AND HTTPS <c>spclient.../event-service/v1/events</c>) both 404 — gabo is the
/// only working transport.
/// </para>
/// <para>
/// v1 ships one envelope per POST (no client-side batching). The desktop
/// client batches ~30 s of events; we can layer that on later if request
/// volume becomes an issue.
/// </para>
/// </remarks>
public sealed class EventService : IAsyncDisposable
{
    private readonly SpClient _spClient;
    private readonly GaboContext _ctx;
    private readonly byte[] _sequenceId;
    private readonly Dictionary<string, long> _sequenceCounters = new();
    private readonly object _seqLock = new();
    private readonly ILogger? _logger;
    private readonly AsyncWorker<IPlaybackEvent> _asyncWorker;
    private readonly Subject<IPlaybackEvent> _eventSubject = new();
    private bool _disposed;

    /// <summary>
    /// In-process subscribers (e.g. a future LibraryPlayRecorder) can listen
    /// to every published event without needing to mirror the network path.
    /// </summary>
    public IObservable<IPlaybackEvent> Events => _eventSubject.AsObservable();

    /// <summary>
    /// Creates the EventService. <paramref name="installationId"/> should be
    /// stable across runs (16-byte random per install). For now Wavee derives
    /// it from the device id — same effect for play history attribution.
    /// </summary>
    public EventService(
        SpClient spClient,
        string deviceIdHex,
        string clientIdHex,
        ReadOnlySpan<byte> installationId,
        string osVersion,
        string deviceManufacturer,
        string deviceModel,
        string osLevelDeviceId,
        ReadOnlySpan<byte> appSessionId,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(spClient);
        ArgumentException.ThrowIfNullOrEmpty(deviceIdHex);
        ArgumentException.ThrowIfNullOrEmpty(clientIdHex);

        _spClient = spClient;
        _logger = logger;

        _ctx = new GaboContext
        {
            ClientIdBytes = HexOrEmpty(clientIdHex),
            InstallationIdBytes = installationId.ToArray(),
            ClientContextIdBytes = RandomNumberGenerator.GetBytes(20),
            AppVersionString = "1.2.88.483",
            AppVersionCode = 128800483L,
            AppSessionIdBytes = appSessionId.ToArray(),
            PlatformType = "windows",
            // Match desktop's context_device_desktop verbatim — server-side
            // anti-fraud checks gate batches on these strings. Fake values
            // ("Wavee" / "Wavee Desktop") cause every event in the batch to
            // get rejected with reason=3.
            DeviceManufacturer = deviceManufacturer,
            DeviceModel = deviceModel,
            DeviceIdString = osLevelDeviceId,
            OsVersion = osVersion,
        };

        _sequenceId = RandomNumberGenerator.GetBytes(20);

        _asyncWorker = new AsyncWorker<IPlaybackEvent>(
            "EventService",
            SendEventInternalAsync,
            logger);
    }

    /// <summary>
    /// Queues an event for asynchronous send. Local subscribers see it
    /// immediately via the <see cref="Events"/> observable; the gabo POST
    /// happens on the worker thread.
    /// </summary>
    public void SendEvent(IPlaybackEvent playbackEvent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(playbackEvent);

        _eventSubject.OnNext(playbackEvent);

        if (!_asyncWorker.TrySubmit(playbackEvent))
        {
            _logger?.LogWarning("Event queue full, dropping event: {EventType}",
                playbackEvent.GetType().Name);
        }
        else
        {
            _logger?.LogDebug("Event queued: {EventType}", playbackEvent.GetType().Name);
        }
    }

    /// <summary>
    /// Dispatches a group of events as ONE gabo POST. Mirrors the desktop
    /// client's batching: a single <see cref="PublishEventsRequest"/> carries
    /// every event in a per-track-start or per-track-end group.
    /// </summary>
    public void SendEventBatch(IReadOnlyList<IPlaybackEvent> events)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0) return;

        foreach (var ev in events) _eventSubject.OnNext(ev);

        var batch = new EventBatch(events);
        if (!_asyncWorker.TrySubmit(batch))
        {
            _logger?.LogWarning("Event queue full, dropping batch of {Count}", events.Count);
        }
        else
        {
            _logger?.LogDebug("Event batch queued: {Count} events", events.Count);
        }
    }

    private async ValueTask SendEventInternalAsync(IPlaybackEvent playbackEvent)
    {
        try
        {
            var request = new PublishEventsRequest { SuppressPersist = false };
            int eventCount;

            if (playbackEvent is EventBatch batch)
            {
                foreach (var ev in batch.Events)
                {
                    var seq = NextSequenceNumber(ev.GetType().Name);
                    request.Event.Add(ev.Build(_ctx, _sequenceId, seq));
                }
                eventCount = batch.Events.Count;
            }
            else
            {
                var seq = NextSequenceNumber(playbackEvent.GetType().Name);
                request.Event.Add(playbackEvent.Build(_ctx, _sequenceId, seq));
                eventCount = 1;
            }

            var bytes = request.ToByteArray();
            await _spClient.PostGaboEventAsync(bytes);

            _logger?.LogDebug("gabo POST: {Count} envelope(s), {Bytes} bytes",
                eventCount, bytes.Length);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to send gabo POST: {EventType}",
                playbackEvent.GetType().Name);
        }
    }

    /// <summary>
    /// Marker carrier for batch dispatch through <see cref="AsyncWorker{T}"/>.
    /// Never materialized into an envelope on its own; <see cref="SendEventInternalAsync"/>
    /// type-checks for it and unwraps the contained events.
    /// </summary>
    private sealed record EventBatch(IReadOnlyList<IPlaybackEvent> Events) : IPlaybackEvent
    {
        public EventEnvelope Build(GaboContext ctx, byte[] seqId, long seqNum)
            => throw new InvalidOperationException("EventBatch.Build called directly");
    }

    private long NextSequenceNumber(string eventName)
    {
        lock (_seqLock)
        {
            _sequenceCounters.TryGetValue(eventName, out var current);
            current++;
            _sequenceCounters[eventName] = current;
            return current;
        }
    }

    private static byte[] HexOrEmpty(string hex)
    {
        try { return Convert.FromHexString(hex); }
        catch { return Array.Empty<byte>(); }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _eventSubject.OnCompleted();
        _eventSubject.Dispose();

        try
        {
            await _asyncWorker.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "EventService disposal hiccup");
        }
    }
}

