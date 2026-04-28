using Google.Protobuf;
using Wavee.Protocol.EventSender;
using Wavee.Protocol.EventSender.Events;

namespace Wavee.Connect.Events;

/// <summary>
/// Maps the per-play <c>command_id</c> (32 lowercase hex chars) to the
/// <c>playback_id</c> (16 bytes) it kicked off. Desktop emits this in the
/// track-start batch; the same <c>command_id</c> appears in the closing
/// <c>RawCoreStream</c> at field 63 and in <c>AudioResolve</c> events.
/// </summary>
public sealed class CorePlaybackCommandCorrelationEvent : IPlaybackEvent
{
    private const string EventName = "CorePlaybackCommandCorrelation";

    private readonly byte[] _playbackId;
    private readonly string _commandIdHex;

    public CorePlaybackCommandCorrelationEvent(byte[] playbackId, string commandIdHex)
    {
        _playbackId = playbackId;
        _commandIdHex = commandIdHex;
    }

    /// <inheritdoc />
    public EventEnvelope Build(GaboContext ctx, byte[] sequenceId, long sequenceNumber)
    {
        var msg = new CorePlaybackCommandCorrelation
        {
            PlaybackId = ByteString.CopyFrom(_playbackId),
            CommandId = _commandIdHex,
        };

        return GaboEnvelopeFactory.BuildEnvelope(EventName, msg.ToByteArray(), ctx, sequenceId, sequenceNumber);
    }
}
