namespace Wavee.Connect.Events;

/// <summary>
/// Event sent when a new playback ID is created (new track starts).
/// </summary>
/// <remarks>
/// Based on librespot-java's NewPlaybackIdEvent.
/// Format: 558|1|playbackId|sessionId|timestamp
/// </remarks>
public sealed class NewPlaybackIdEvent : IPlaybackEvent
{
    private readonly string _sessionId;
    private readonly string _playbackId;

    /// <summary>
    /// Creates a new NewPlaybackIdEvent.
    /// </summary>
    /// <param name="sessionId">Current session ID.</param>
    /// <param name="playbackId">New playback ID (32-char hex).</param>
    public NewPlaybackIdEvent(string sessionId, string playbackId)
    {
        _sessionId = sessionId;
        _playbackId = playbackId;
    }

    /// <inheritdoc />
    public EventBuilder Build()
    {
        var builder = new EventBuilder(EventType.NewPlaybackId);
        builder.Append(_playbackId);
        builder.Append(_sessionId);
        builder.Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return builder;
    }
}
