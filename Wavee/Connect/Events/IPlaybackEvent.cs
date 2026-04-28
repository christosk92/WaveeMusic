using Wavee.Protocol.EventSender;

namespace Wavee.Connect.Events;

/// <summary>
/// A single playback event that <see cref="EventService"/> can wrap into a
/// <see cref="EventEnvelope"/> and POST to gabo-receiver-service.
/// </summary>
public interface IPlaybackEvent
{
    /// <summary>
    /// Builds the event into an <see cref="EventEnvelope"/> ready to be added
    /// to a <see cref="PublishEventsRequest"/>.
    /// </summary>
    /// <param name="ctx">Static device/client context shared across the session.</param>
    /// <param name="sequenceId">20-byte session-scoped sequence id.</param>
    /// <param name="sequenceNumber">Monotonically incrementing per-event-name within the sequence.</param>
    EventEnvelope Build(GaboContext ctx, byte[] sequenceId, long sequenceNumber);
}
