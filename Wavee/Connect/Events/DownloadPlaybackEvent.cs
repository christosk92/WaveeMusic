using Google.Protobuf;
using Wavee.Protocol.EventSender;
using Wavee.Protocol.EventSender.Events;

namespace Wavee.Connect.Events;

/// <summary>
/// Per-CDN-fetch metrics. Desktop emits one of these in the track-end batch
/// for every audio file it actually pulled from a CDN (skipped for cache hits).
/// Many fields are unknown-semantic histograms; we fill the ones whose meaning
/// we know and zero / sentinel-Max-uint64 the rest. Spotify treats
/// <c>UInt64.MaxValue</c> as "not measured" — observed in desktop wire.
/// </summary>
public sealed class DownloadPlaybackEvent : IPlaybackEvent
{
    private const string EventName = "Download";
    private const string Realm = "music";
    private const string CdnUriScheme = "https";
    private const string SocketReuse = "unknown";
    private const string InitialDiskState = "missing";
    private const string ErrorKind = "unknown";
    private const ulong NotMeasured = ulong.MaxValue;

    private readonly byte[] _fileId;
    private readonly byte[] _playbackId;
    private readonly long _fileSize;
    private readonly long _bytesDownloaded;
    private readonly string _cdnDomain;
    private readonly string _requestType;
    private readonly long _bitrate;
    private readonly long _httpLatencyMs;
    private readonly long _totalTimeMs;

    public DownloadPlaybackEvent(
        byte[] fileId,
        byte[] playbackId,
        long fileSize,
        long bytesDownloaded,
        string cdnDomain,
        string requestType,
        long bitrate,
        long httpLatencyMs,
        long totalTimeMs)
    {
        _fileId = fileId;
        _playbackId = playbackId;
        _fileSize = fileSize;
        _bytesDownloaded = bytesDownloaded;
        _cdnDomain = cdnDomain;
        _requestType = requestType;
        _bitrate = bitrate;
        _httpLatencyMs = httpLatencyMs;
        _totalTimeMs = totalTimeMs;
    }

    /// <inheritdoc />
    public EventEnvelope Build(GaboContext ctx, byte[] sequenceId, long sequenceNumber)
    {
        var msg = new Download
        {
            FileId = ByteString.CopyFrom(_fileId),
            PlaybackId = ByteString.CopyFrom(_playbackId),
            FileSize = _fileSize,
            BytesWasted = 0,
            BytesDownloaded = _bytesDownloaded,
            Realm = Realm,
            Sentinel12 = NotMeasured,
            Sentinel13 = NotMeasured,
            Sentinel14 = NotMeasured,
            Sentinel15 = NotMeasured,
            Sentinel16 = NotMeasured,
            Sentinel17 = NotMeasured,
            CdnUriScheme = CdnUriScheme,
            CdnDomain = _cdnDomain,
            SocketReuse = SocketReuse,
            BytesFromCache = 0,
            BytesPredownloaded = 0,
            BytesStreamed = _bytesDownloaded,
            TotalBytes = _fileSize,
            Errors = 0,
            RequestType = _requestType,
            HttpLatencyMs = _httpLatencyMs,
            Bitrate = _bitrate,
            CdnCount = 1,
            RetryCount = 0,
            InitialDiskState = InitialDiskState,
            RequestCount = 1,
            ErrorKind = ErrorKind,
            TotalTimeMs = _totalTimeMs,
        };

        return GaboEnvelopeFactory.BuildEnvelope(EventName, msg.ToByteArray(), ctx, sequenceId, sequenceNumber);
    }
}
