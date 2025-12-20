namespace Wavee.Connect.Events;

/// <summary>
/// Event sent when a new session ID is created (context changes).
/// </summary>
/// <remarks>
/// Based on librespot-java's NewSessionIdEvent.
/// Format: 557|3|sessionId|contextUri|contextUri|timestamp||contextSize|contextUrl
/// </remarks>
public sealed class NewSessionIdEvent : IPlaybackEvent
{
    private readonly string _sessionId;
    private readonly string _contextUri;
    private readonly int _contextSize;
    private readonly string _contextUrl;

    /// <summary>
    /// Creates a new NewSessionIdEvent.
    /// </summary>
    /// <param name="sessionId">New session ID.</param>
    /// <param name="contextUri">Context URI (playlist, album, track).</param>
    /// <param name="contextSize">Number of tracks in context.</param>
    /// <param name="contextUrl">Context URL (can be empty).</param>
    public NewSessionIdEvent(
        string sessionId,
        string contextUri,
        int contextSize = 1,
        string contextUrl = "")
    {
        _sessionId = sessionId;
        _contextUri = contextUri;
        _contextSize = contextSize;
        _contextUrl = contextUrl;
    }

    /// <inheritdoc />
    public EventBuilder Build()
    {
        var builder = new EventBuilder(EventType.NewSessionId);
        builder.Append(_sessionId);
        builder.Append(_contextUri);
        builder.Append(_contextUri); // Appears twice in librespot-java
        builder.Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        builder.Append(""); // Empty field
        builder.Append(_contextSize);
        builder.Append(_contextUrl);
        return builder;
    }
}
