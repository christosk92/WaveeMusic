using Google.Protobuf;
using Wavee.Protocol.EventSender;
using Wavee.Protocol.EventSender.Events;

namespace Wavee.Connect.Events;

/// <summary>
/// Anti-ripping attestation event sent in the same gabo batch as the closing
/// <see cref="RawCoreStreamPlaybackEvent"/>. Spotify's play-history pipeline
/// requires this event per playback_id — without it (or with the ripping
/// flags set) the play is dropped before reaching Recently Played.
/// </summary>
/// <remarks>
/// Schema and field semantics verified by structural-decoding three real
/// ContentIntegrity envelopes from spot SAZ session 291_c.txt (Spotify
/// desktop 1.2.88.483, captured 2026-04-28). All three carried
/// <c>ripping_categories=0</c> and <c>is_ripping_faster_than_rt=false</c>.
/// We send the same honest values — Wavee plays in real time, no rip.
/// </remarks>
public sealed class ContentIntegrityPlaybackEvent : IPlaybackEvent
{
    private const string EventName = "ContentIntegrity";

    private readonly string _playbackIdHex;

    public ContentIntegrityPlaybackEvent(string playbackIdHex)
    {
        _playbackIdHex = playbackIdHex ?? string.Empty;
    }

    /// <inheritdoc />
    public EventEnvelope Build(GaboContext ctx, byte[] sequenceId, long sequenceNumber)
    {
        var msg = new ContentIntegrity
        {
            PlaybackId = HexToByteString(_playbackIdHex),
            RippingCategories = 0,
            IsRippingFasterThanRt = false,
        };
        return GaboEnvelopeFactory.BuildEnvelope(
            EventName, msg.ToByteArray(), ctx, sequenceId, sequenceNumber);
    }

    private static ByteString HexToByteString(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return ByteString.Empty;
        try { return ByteString.CopyFrom(Convert.FromHexString(hex)); }
        catch { return ByteString.Empty; }
    }
}
