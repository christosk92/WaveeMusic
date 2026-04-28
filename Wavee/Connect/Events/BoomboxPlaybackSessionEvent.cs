using Google.Protobuf;
using Wavee.Protocol.EventSender;
using Wavee.Protocol.EventSender.Events;

namespace Wavee.Connect.Events;

/// <summary>
/// Per-track session summary. Desktop emits this in the track-start batch
/// once the playback_id is minted. Many fields are unknown-semantic counters;
/// we send zeros for those and fill the named ones from orchestrator state.
/// Inferred semantics from <c>062_c.txt</c>:
///   audio_key_ms=269, resolve_ms=121, total_setup_ms=405, buffering_ms=248,
///   duration_ms=262960, preset="default", subsystem="media-player",
///   interruption="none", first_play=1.
/// </summary>
public sealed class BoomboxPlaybackSessionEvent : IPlaybackEvent
{
    private const string EventName = "BoomboxPlaybackSession";
    private const string Preset = "default";
    private const string Subsystem = "media-player";
    private const string Interruption = "none";

    private readonly byte[] _playbackId;
    private readonly long _audioKeyMs;
    private readonly long _resolveMs;
    private readonly long _totalSetupMs;
    private readonly long _bufferingMs;
    private readonly long _durationMs;
    private readonly bool _firstPlay;

    public BoomboxPlaybackSessionEvent(
        byte[] playbackId,
        long audioKeyMs,
        long resolveMs,
        long totalSetupMs,
        long bufferingMs,
        long durationMs,
        bool firstPlay)
    {
        _playbackId = playbackId;
        _audioKeyMs = audioKeyMs;
        _resolveMs = resolveMs;
        _totalSetupMs = totalSetupMs;
        _bufferingMs = bufferingMs;
        _durationMs = durationMs;
        _firstPlay = firstPlay;
    }

    /// <inheritdoc />
    public EventEnvelope Build(GaboContext ctx, byte[] sequenceId, long sequenceNumber)
    {
        var msg = new BoomboxPlaybackSession
        {
            PlaybackId = ByteString.CopyFrom(_playbackId),
            AudioKeyMs = _audioKeyMs,
            ResolveMs = _resolveMs,
            TotalSetupMs = _totalSetupMs,
            BufferingMs = _bufferingMs,
            DurationMs = _durationMs,
            Preset = Preset,
            Subsystem = Subsystem,
            Interruption = Interruption,
            FirstPlay = _firstPlay,
        };

        return GaboEnvelopeFactory.BuildEnvelope(EventName, msg.ToByteArray(), ctx, sequenceId, sequenceNumber);
    }
}
