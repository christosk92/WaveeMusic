using System.Security.Cryptography;
using Google.Protobuf;
using Wavee.Core.Audio;
using Wavee.Protocol.EventSender;
using Wavee.Protocol.EventSender.Events;

namespace Wavee.Connect.Events;

/// <summary>
/// Per-track end-of-play event that populates Spotify's Recently Played and
/// drives play counts. Posted to <c>gabo-receiver-service/v3/events/</c> as a
/// <see cref="RawCoreStream"/> protobuf wrapped in an <see cref="EventEnvelope"/>.
/// Built from the orchestrator's <see cref="PlaybackMetrics"/> + a final
/// reason_end at the moment the track stops/skips/finishes.
/// </summary>
public sealed class RawCoreStreamPlaybackEvent : IPlaybackEvent
{
    private const string EventName = "RawCoreStream";
    // Wire-observed (032_c.txt, 064_c.txt) on Spotify desktop 1.2.88.483:
    // playback_stack is lowercase "boombox" (NOT "BOOMBOX"); core_version is a
    // huge int64 (~6e15); orchestration_stack is "context-player". Field
    // identifier and version (#6/#7) are absent from the desktop wire — we omit.
    private const long CoreVersion = 6003700000000000L;
    private const string PlaybackStack = "boombox";
    private const string OrchestrationStack = "context-player";

    private readonly PlaybackMetrics _metrics;
    private readonly string _trackUri;
    private readonly string _connectControllerDeviceId;
    private readonly bool _isCachedHit;
    private readonly string _contextKind;
    private readonly string _commandIdHex;
    private readonly string _nextTrackBase62;

    public RawCoreStreamPlaybackEvent(
        PlaybackMetrics metrics,
        string trackUri,
        string connectControllerDeviceId,
        bool isCachedHit,
        string contextKind,
        string commandIdHex,
        string nextTrackBase62)
    {
        _metrics = metrics;
        _trackUri = trackUri;
        _connectControllerDeviceId = connectControllerDeviceId;
        _isCachedHit = isCachedHit;
        _contextKind = string.IsNullOrEmpty(contextKind) ? "unknown" : contextKind;
        _commandIdHex = commandIdHex ?? string.Empty;
        _nextTrackBase62 = nextTrackBase62 ?? string.Empty;
    }

    /// <inheritdoc />
    public EventEnvelope Build(GaboContext ctx, byte[] sequenceId, long sequenceNumber)
    {
        var raw = BuildRawCoreStream();
        return GaboEnvelopeFactory.BuildEnvelope(
            EventName, raw.ToByteArray(), ctx, sequenceId, sequenceNumber);
    }

    private RawCoreStream BuildRawCoreStream()
    {
        var msg = new RawCoreStream
        {
            PlaybackId = HexToByteString(_metrics.PlaybackId),
            MediaType = string.IsNullOrWhiteSpace(_metrics.MediaType) ? "audio" : _metrics.MediaType,
            // Desktop omits feature_identifier (#6) and feature_version (#7) on
            // RawCoreStream — leave both unset so they don't appear on the wire.
            // source_start / source_end are the CONTEXT KIND on desktop
            // ("playlist"/"album"/"artist"/...), NOT a device id.
            SourceStart = _contextKind,
            ReasonStart = _metrics.ReasonStart?.ToEventValue() ?? "unknown",
            SourceEnd = _contextKind,
            ReasonEnd = _metrics.ReasonEnd?.ToEventValue() ?? "unknown",
            PlaybackStartTime = _metrics.Timestamp,
            MsPlayed = ComputeMsPlayed(),
            MsPlayedNominal = ComputeMsPlayed(),
            AudioFormat = FormatAudioFormat(),
            PlayContext = _metrics.ContextUri,
            ContentUri = _trackUri,
            Provider = "context",
            Referrer = string.IsNullOrEmpty(_metrics.ReferrerIdentifier) ? _contextKind : _metrics.ReferrerIdentifier,
            // Desktop omits #33 ConnectControllerDeviceId on RawCoreStream — leave empty.
            // Wire field #38 carries play_type "full" (NOT core_bundle as the
            // schema extraction suggested). Real core_bundle is at #44.
            PlayType = "full",
            // Desktop sends core_bundle="local" at #44 unconditionally — verified
            // across both cached and streamed plays in the SAZ capture.
            CoreBundle = "local",
            // Desktop wire #39 = 1 — Premium account flag (we usually are Premium).
            IsAssumedPremium = true,
            CoreVersion = CoreVersion,
            PlaybackStack = PlaybackStack,
            // Wire field #48: desktop puts the storage-resolve session pointer
            // here as a string "ssp~<32 hex>". Schema labels it decision_id.
            // We don't go through the storage-resolve flow (PlayPlay obfuscation
            // path), so synthesize a plausibly-shaped value.
            DecisionId = "ssp~" + RandomHex32(),
            NextContentUriBase62 = _nextTrackBase62,
            CommandId = _commandIdHex,
            // playback_stack again at #69 (desktop quirk — same value).
            PlaybackStackSecondary = PlaybackStack,
            OrchestrationStack = OrchestrationStack,
        };

        if (_metrics.Player?.ContentMetrics?.FileId is { Length: > 0 } fileIdHex)
        {
            try { msg.MediaId = ByteString.CopyFrom(Convert.FromHexString(fileIdHex)); }
            catch { /* malformed — leave empty rather than crash */ }
        }

        return msg;
    }

    private static string RandomHex32()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private int ComputeMsPlayed()
    {
        var first = _metrics.FirstValue;
        var last = _metrics.LastValue;
        var delta = last - first;
        return delta > 0 ? delta : 0;
    }

    private string FormatAudioFormat()
    {
        var bitrate = _metrics.Player?.Bitrate ?? 0;
        var codec = _metrics.Player?.Encoding ?? "vorbis";
        // Spotify desktop emits strings like "Vorbis 160 kbps" / "Vorbis 320 kbps".
        var codecPretty = codec.Equals("vorbis", StringComparison.OrdinalIgnoreCase) ? "Vorbis" : codec;
        var kbps = bitrate > 0 ? bitrate / 1000 : 320;
        return $"{codecPretty} {kbps} kbps";
    }

    private static ByteString HexToByteString(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return ByteString.Empty;
        try { return ByteString.CopyFrom(Convert.FromHexString(hex)); }
        catch { return ByteString.Empty; }
    }
}
