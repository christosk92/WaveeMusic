namespace Wavee.Connect.Events;

/// <summary>
/// Interface for playback events that can be sent to Spotify's event-service.
/// </summary>
public interface IPlaybackEvent
{
    /// <summary>
    /// Builds the event into an EventBuilder ready for transmission.
    /// </summary>
    EventBuilder Build();
}
