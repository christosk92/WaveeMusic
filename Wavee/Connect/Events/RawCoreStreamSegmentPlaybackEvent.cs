using Google.Protobuf;
using Wavee.Protocol.EventSender;
using Wavee.Protocol.EventSender.Events;

namespace Wavee.Connect.Events;

/// <summary>
/// RawCoreStreamSegment — one per pause/resume/seek split within a single
/// track. The desktop's StreamReporter emits these via
/// <c>sendStreamStart</c> / <c>sendStreamSeek</c> / per-segment internal
/// transitions. The server uses the segment timeline to validate the play
/// (e.g. that the user actually listened, not just left the page open).
/// Without segments, the closing RawCoreStream looks like a 0-duration play.
/// </summary>
public sealed class RawCoreStreamSegmentPlaybackEvent : IPlaybackEvent
{
    private const string EventName = "RawCoreStreamSegment";
    // Wire-observed lowercase + bumped core_version per RawCoreStream parity.
    private const string PlaybackStack = "boombox";
    private const string Provider = "context";
    private const long CoreVersion = 6003700000000000L;

    private readonly string _playbackIdHex;
    private readonly string _trackUri;
    private readonly string _contextUri;
    private readonly long _startPositionMs;
    private readonly long _endPositionMs;
    private readonly long _startTimestampMs;
    private readonly long _endTimestampMs;
    private readonly string _reasonStart;
    private readonly string _reasonEnd;
    private readonly bool _isPause;
    private readonly bool _isSeek;
    private readonly bool _isLast;
    private readonly long _sequenceId;
    private readonly string _mediaType;

    public RawCoreStreamSegmentPlaybackEvent(
        string playbackIdHex,
        string trackUri,
        string contextUri,
        long startPositionMs,
        long endPositionMs,
        long startTimestampMs,
        long endTimestampMs,
        string reasonStart,
        string reasonEnd,
        bool isPause,
        bool isSeek,
        bool isLast,
        long sequenceId,
        string mediaType = "audio")
    {
        _playbackIdHex = playbackIdHex;
        _trackUri = trackUri;
        _contextUri = contextUri;
        _startPositionMs = startPositionMs;
        _endPositionMs = endPositionMs;
        _startTimestampMs = startTimestampMs;
        _endTimestampMs = endTimestampMs;
        _reasonStart = reasonStart;
        _reasonEnd = reasonEnd;
        _isPause = isPause;
        _isSeek = isSeek;
        _isLast = isLast;
        _sequenceId = sequenceId;
        _mediaType = string.IsNullOrWhiteSpace(mediaType) ? "audio" : mediaType;
    }

    /// <inheritdoc />
    public EventEnvelope Build(GaboContext ctx, byte[] sequenceId, long sequenceNumber)
    {
        var msPlayed = (int)Math.Max(0, _endPositionMs - _startPositionMs);
        var msg = new RawCoreStreamSegment
        {
            PlaybackId = HexToByteString(_playbackIdHex),
            StartPosition = _startPositionMs,
            EndPosition = _endPositionMs,
            MsPlayed = msPlayed,
            ReasonStart = _reasonStart,
            ReasonEnd = _reasonEnd,
            PlaybackSpeed = 1.0,
            StartTimestamp = _startTimestampMs,
            EndTimestamp = _endTimestampMs,
            IsSeek = _isSeek,
            IsPause = _isPause,
            SequenceId = _sequenceId,
            MediaType = _mediaType,
            CoreVersion = CoreVersion,
            ContentUri = _trackUri,
            IsLast = _isLast,
            Provider = Provider,
            PlaybackStack = PlaybackStack,
            PlayContext = _contextUri,
            IsAudioOn = true,
            IsVideoOn = string.Equals(_mediaType, "video", StringComparison.OrdinalIgnoreCase),
        };

        return GaboEnvelopeFactory.BuildEnvelope(EventName, msg.ToByteArray(), ctx, sequenceId, sequenceNumber);
    }

    private static ByteString HexToByteString(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return ByteString.Empty;
        try { return ByteString.CopyFrom(Convert.FromHexString(hex)); }
        catch { return ByteString.Empty; }
    }
}
