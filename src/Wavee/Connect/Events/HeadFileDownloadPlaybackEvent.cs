using Google.Protobuf;
using Wavee.Protocol.EventSender;
using Wavee.Protocol.EventSender.Events;

namespace Wavee.Connect.Events;

/// <summary>
/// Head-file (first 128 KB) download metrics. Desktop emits one in the track-end
/// batch for every audio file actually fetched. <see cref="HeadFileSize"/> is
/// always 128 KiB on desktop; we mirror that. The latency values default to the
/// desktop-observed range (~220–270 ms) when the orchestrator hasn't measured
/// the real fetch.
/// </summary>
public sealed class HeadFileDownloadPlaybackEvent : IPlaybackEvent
{
    private const string EventName = "HeadFileDownload";
    private const string CdnUriScheme = "https";
    private const string CdnDomain = "heads-fa-tls13.spotifycdn.com";
    private const long HeadFileSize = 131072;
    private const string SocketReuse = "unknown";
    private const string RequestType = "interactive";
    private const string InitialDiskState = "missing";

    private readonly byte[] _fileId;
    private readonly byte[] _playbackId;
    private readonly long _httpLatencyMs;
    private readonly long _http64kLatencyMs;
    private readonly long _totalTimeMs;
    private readonly long _httpResult;

    public HeadFileDownloadPlaybackEvent(
        byte[] fileId,
        byte[] playbackId,
        long httpLatencyMs,
        long http64kLatencyMs,
        long totalTimeMs,
        long httpResult)
    {
        _fileId = fileId;
        _playbackId = playbackId;
        _httpLatencyMs = httpLatencyMs;
        _http64kLatencyMs = http64kLatencyMs;
        _totalTimeMs = totalTimeMs;
        _httpResult = httpResult;
    }

    /// <inheritdoc />
    public EventEnvelope Build(GaboContext ctx, byte[] sequenceId, long sequenceNumber)
    {
        var msg = new HeadFileDownload
        {
            FileId = ByteString.CopyFrom(_fileId),
            PlaybackId = ByteString.CopyFrom(_playbackId),
            CdnUriScheme = CdnUriScheme,
            CdnDomain = CdnDomain,
            HeadFileSize = HeadFileSize,
            BytesDownloaded = HeadFileSize,
            BytesWasted = 0,
            HttpLatency = _httpLatencyMs,
            Http64KLatency = _http64kLatencyMs,
            TotalTime = _totalTimeMs,
            HttpResult = _httpResult,
            ErrorCode = 0,
            CachedBytes = HeadFileSize,
            BytesFromCache = 0,
            SocketReuse = SocketReuse,
            RequestType = RequestType,
            InitialDiskState = InitialDiskState,
        };

        return GaboEnvelopeFactory.BuildEnvelope(EventName, msg.ToByteArray(), ctx, sequenceId, sequenceNumber);
    }
}
