using Google.Protobuf;
using Wavee.Protocol.EventSender;
using Wavee.Protocol.EventSender.Events;

namespace Wavee.Connect.Events;

/// <summary>
/// AudioSessionEvent — the FSM book-end events Spotify desktop emits around
/// every track. Desktop sequence per track:
///
///   open  → (resume → pause)* → seek* → close
///
/// These pair up server-side with the closing <see cref="RawCoreStreamPlaybackEvent"/>;
/// if open/close don't show up, Spotify's stream_reporting FSM (verified in
/// Spotify.dll: <c>StreamReporter: FSM Transition</c>) probably treats the
/// final RawCoreStream as orphaned and filters the play out.
/// </summary>
public sealed class AudioSessionPlaybackEvent : IPlaybackEvent
{
    private const string EventName = "AudioSessionEvent";
    private const string FeatureIdentifier = "boombox";

    private readonly string _eventKind;       // "open" / "close" / "resume" / "pause" / "seek"
    private readonly string _playbackIdHex;   // 32-char hex playback id
    private readonly string? _reason;         // for close: the reason_end ("trackdone"/"fwdbtn"/...)
    private readonly string? _context;        // play context kind, e.g. "playlist" / "album"
    private readonly int? _seekPosition;      // for seek events

    private AudioSessionPlaybackEvent(string kind, string playbackIdHex, string? reason = null,
                                       string? context = null, int? seekPosition = null)
    {
        _eventKind = kind;
        _playbackIdHex = playbackIdHex;
        _reason = reason;
        _context = context;
        _seekPosition = seekPosition;
    }

    public static AudioSessionPlaybackEvent Open(string playbackIdHex, string? context)
        => new("open", playbackIdHex, context: context);

    public static AudioSessionPlaybackEvent Close(string playbackIdHex, string reasonEnd, string? context)
        => new("close", playbackIdHex, reason: reasonEnd, context: context);

    public static AudioSessionPlaybackEvent Pause(string playbackIdHex)
        => new("pause", playbackIdHex);

    public static AudioSessionPlaybackEvent Resume(string playbackIdHex)
        => new("resume", playbackIdHex);

    public static AudioSessionPlaybackEvent Seek(string playbackIdHex, int positionMs)
        => new("seek", playbackIdHex, seekPosition: positionMs);

    /// <inheritdoc />
    public EventEnvelope Build(GaboContext ctx, byte[] sequenceId, long sequenceNumber)
    {
        var msg = new AudioSessionEvent
        {
            Event = _eventKind,
            PlaybackId = HexToByteString(_playbackIdHex),
            FeatureIdentifier = FeatureIdentifier,
        };
        if (!string.IsNullOrEmpty(_reason)) msg.Reason = _reason;
        if (!string.IsNullOrEmpty(_context)) msg.Context = _context;
        if (_seekPosition is { } pos) msg.SeekPosition = pos;

        return GaboEnvelopeFactory.BuildEnvelope(EventName, msg.ToByteArray(), ctx, sequenceId, sequenceNumber);
    }

    private static ByteString HexToByteString(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return ByteString.Empty;
        try { return ByteString.CopyFrom(Convert.FromHexString(hex)); }
        catch { return ByteString.Empty; }
    }
}
