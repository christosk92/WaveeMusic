using System;
using System.Security.Cryptography;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Core;
using Wavee.Protocol.EventSender.Events;
using Wavee.SpotifyLive.Gabo;

namespace Wavee.SpotifyLive;

/// <summary>Gabo play-registration projection: RawCoreStream + segments + ContentIntegrity + siblings.</summary>
public sealed class RawCoreStreamProjection : IPlaybackProjection, IAsyncDisposable
{
    const long CoreVersion = 6_004_800_000_000_000L;
    const string PlaybackStack = "boombox";
    const string OrchestrationStack = "context-player";

    readonly GaboBatcher _batcher;
    readonly Func<string?> _contextUri;
    readonly Func<bool> _isPremium;

    PlaybackIds? _ids;
    string _contentUri = "";
    string _playContext = "";
    string _provider = "context";
    string _reasonStart = "clickrow";
    string _sourceStart = "playlist";
    long _trackStartMs;
    long _segmentStartMs;
    int _segmentStartPosMs;
    int _msPlayed;
    long _segmentSeq;
    long _segmentInternalSeq;
    bool _firstPlayInSession = true;
    byte[]? _mediaId;
    byte[]? _fileId;
    int _bitrateKbps;
    string _audioFormat = "";
    long _durationMs;

    public RawCoreStreamProjection(GaboBatcher batcher, Func<string?>? contextUri = null,
        Func<bool>? isPremium = null)
    {
        _batcher = batcher;
        _contextUri = contextUri ?? (() => null);
        _isPremium = isPremium ?? (() => true);
    }

    public void OnEvent(in PlaybackEvent e)
    {
        switch (e.Kind)
        {
            case EvKind.Started:
            case EvKind.TrackChanged:
                if (e.Track is null || e.Ids is null) return;
                EndPreviousIfNeeded(e.AtMs, "trackdone");
                BeginTrack(in e);
                break;
            case EvKind.Paused:
                CloseSegment((int)Math.Min(e.AtMs, int.MaxValue), isPause: true, isLast: false, reasonEnd: "pause");
                EmitAudioSession("pause", e.Ids?.PlaybackId);
                break;
            case EvKind.Resumed:
                _reasonStart = "playbtn";
                _segmentStartMs = NowMs();
                _segmentStartPosMs = (int)Math.Min(e.AtMs, int.MaxValue);
                EmitAudioSession("resume", e.Ids?.PlaybackId);
                break;
            case EvKind.Seeked:
            {
                int closePos = (int)Math.Min(e.AtMs, int.MaxValue);
                int targetPos = e.SeekToMs >= 0 ? (int)Math.Min(e.SeekToMs, int.MaxValue) : closePos;
                CloseSegment(closePos, isPause: false, isLast: false, reasonEnd: "seek");
                _reasonStart = "playbtn";
                _segmentStartMs = NowMs();
                _segmentStartPosMs = targetPos;
                EmitAudioSession("seek", e.Ids?.PlaybackId, seekPosition: targetPos);
                break;
            }
            case EvKind.Ended:
                if (_ids is not null) DispatchTrackEnd(e.AtMs, e.ReasonEnd.Length > 0 ? e.ReasonEnd : "endplay");
                ResetTrack();
                break;
        }
    }

    void BeginTrack(in PlaybackEvent e)
    {
        _ids = e.Ids;
        _contentUri = e.Track!.Uri;
        _playContext = _contextUri() ?? e.Track.Uri;
        _provider = string.IsNullOrEmpty(e.Provider) ? "context" : e.Provider;
        _reasonStart = string.IsNullOrEmpty(e.ReasonStart) ? "clickrow" : e.ReasonStart;
        _sourceStart = ParseContextKind(_playContext);
        _mediaId = e.MediaId;
        _fileId = e.FileId;
        _bitrateKbps = e.SelectedBitrateKbps;
        _audioFormat = e.AudioFormatName;
        _durationMs = e.DurationMs;
        _trackStartMs = NowMs();
        _segmentStartMs = _trackStartMs;
        _segmentStartPosMs = (int)Math.Min(e.AtMs, int.MaxValue);
        _msPlayed = 0;
        _segmentSeq = 0;
        _segmentInternalSeq = 0;

        var pid = e.Ids!.PlaybackId;
        EmitCorePlaybackCommandCorrelation(pid, e.Ids.CommandIdHex);
        EmitAudioResolve(pid, e.Ids.CommandIdHex, _contentUri);
        EmitAudioFileSelection(pid);
        EmitAudioSession("open", pid);
        EmitBoomboxSession(pid, e.DurationMs);
        if (_fileId is { Length: > 0 }) EmitHeadFileDownload(_fileId, pid);
    }

    void EndPreviousIfNeeded(long atMs, string reason)
    {
        if (_ids is null) return;
        DispatchTrackEnd(atMs, reason);
        ResetTrack();
    }

    void DispatchTrackEnd(long endPosMs, string reasonEnd)
    {
        if (_ids is null) return;
        int endPos = (int)Math.Min(endPosMs, int.MaxValue);
        CloseSegment(endPos, isPause: false, isLast: true, reasonEnd);
        EmitAudioSession("close", _ids.PlaybackId, reasonEnd);
        if (_fileId is { Length: > 0 }) EmitDownload(_fileId, _ids.PlaybackId);
        EmitRawCoreStream(reasonEnd, endPos);
        EmitContentIntegrity(_ids.PlaybackId);
        EmitAudioRouteSegmentEnd(_ids.PlaybackId);
        EmitAdOpportunity(_ids.PlaybackIdHex, _contentUri);
    }

    void CloseSegment(int endPosMs, bool isPause, bool isLast, string reasonEnd)
    {
        if (_ids is null) return;
        int played = Math.Max(0, endPosMs - _segmentStartPosMs);
        _msPlayed += played;
        long endTs = NowMs();
        _segmentSeq++;
        _segmentInternalSeq++;
        var seg = new RawCoreStreamSegment
        {
            PlaybackId = ByteString.CopyFrom(_ids.PlaybackId),
            StartPosition = _segmentStartPosMs,
            EndPosition = endPosMs,
            MsPlayed = played,
            ReasonStart = _reasonStart,
            ReasonEnd = reasonEnd,
            PlaybackSpeed = 1.0,
            StartTimestamp = _segmentStartMs,
            EndTimestamp = endTs,
            IsPause = isPause,
            IsLast = isLast,
            SequenceId = _segmentSeq,
            MediaType = "audio",
            ContentUri = _contentUri,
            Provider = _provider,
            PlaybackStack = PlaybackStack,
            StreamId = ByteString.CopyFrom(_ids.StreamId),
            PageInstanceId = _ids.PageInstanceId,
            InteractionId = _ids.InteractionId,
            PlayContext = _playContext,
            SequenceIdInternal = _segmentInternalSeq,
            DeviceBrand = "spotify",
            DeviceModelName = "PC laptop",
            DeviceTypeName = "computer",
        };
        _batcher.Enqueue("RawCoreStreamSegment", seg.ToByteArray());
        if (!isLast)
        {
            _segmentStartMs = endTs;
            _segmentStartPosMs = endPosMs;
        }
    }

    void EmitRawCoreStream(string reasonEnd, int endPosMs)
    {
        if (_ids is null) return;
        var raw = new RawCoreStream
        {
            PlaybackId = ByteString.CopyFrom(_ids.PlaybackId),
            ParentPlaybackId = ByteString.CopyFrom(new byte[16]),
            MediaId = _mediaId is { Length: > 0 } ? ByteString.CopyFrom(_mediaId) : ByteString.Empty,
            MediaType = "audio",
            SourceStart = _sourceStart,
            ReasonStart = _reasonStart,
            SourceEnd = _sourceStart,
            ReasonEnd = reasonEnd,
            PlaybackStartTime = _trackStartMs,
            MsPlayed = _msPlayed,
            MsPlayedNominal = _msPlayed,
            AudioFormat = FormatAudioFormat(),
            PlayContext = _playContext,
            ContentUri = _contentUri,
            Provider = _provider,
            Referrer = _sourceStart,
            CoreVersion = CoreVersion,
            PlayType = "full",
            IsAssumedPremium = _isPremium(),
            CoreBundle = "local",
            PlaybackStack = PlaybackStack,
            DecisionId = "",
            PlayContextDecisionId = "",
            StreamId = ByteString.CopyFrom(_ids.StreamId),
            CommandId = _ids.CommandIdHex,
            PlaybackStackSecondary = PlaybackStack,
            OrchestrationStack = OrchestrationStack,
            DeviceBrand = "spotify",
            DeviceModelName = "PC laptop",
            DeviceTypeName = "computer",
        };
        _batcher.Enqueue("RawCoreStream", raw.ToByteArray());
    }

    void EmitCorePlaybackCommandCorrelation(byte[] playbackId, string commandId)
    {
        var msg = new CorePlaybackCommandCorrelation
        {
            PlaybackId = ByteString.CopyFrom(playbackId),
            CommandId = commandId,
        };
        _batcher.Enqueue("CorePlaybackCommandCorrelation", msg.ToByteArray());
    }

    void EmitAudioResolve(byte[] playbackId, string commandId, string contentUri)
    {
        var msg = new AudioResolve
        {
            PlaybackId = ByteString.CopyFrom(playbackId),
            ResolveMs = 120,
            ContentUri = contentUri,
            CommandId = commandId,
        };
        _batcher.Enqueue("AudioResolve", msg.ToByteArray());
    }

    void EmitAudioFileSelection(byte[] playbackId)
    {
        var msg = new AudioFileSelection
        {
            PlaybackId = ByteString.CopyFrom(playbackId),
            Reason = "best matching bitrate",
            SelectedBitrate = _bitrateKbps > 0 ? _bitrateKbps * 1000 : 160_000,
            Quality = "high",
            TargetBitrate = _bitrateKbps > 0 ? _bitrateKbps * 1000 : 160_000,
        };
        _batcher.Enqueue("AudioFileSelection", msg.ToByteArray());
    }

    void EmitContentIntegrity(byte[] playbackId)
    {
        var msg = new ContentIntegrity
        {
            PlaybackId = ByteString.CopyFrom(playbackId),
            RippingCategories = 0,
            IsRippingFasterThanRt = false,
        };
        _batcher.Enqueue("ContentIntegrity", msg.ToByteArray());
    }

    void EmitAudioSession(string evt, byte[]? playbackId, string? reason = null, int seekPosition = 0)
    {
        if (playbackId is null) return;
        var msg = new AudioSessionEvent
        {
            Event = evt,
            PlaybackId = ByteString.CopyFrom(playbackId),
            Reason = reason ?? "",
            FeatureIdentifier = "boombox",
            SeekPosition = seekPosition,
            Paused = evt == "pause",
            Speed = evt == "open" ? long.MaxValue : 1,
        };
        _batcher.Enqueue("AudioSessionEvent", msg.ToByteArray());
    }

    void EmitBoomboxSession(byte[] playbackId, long durationMs)
    {
        var msg = new BoomboxPlaybackSession
        {
            PlaybackId = ByteString.CopyFrom(playbackId),
            AudioKeyMs = 200,
            ResolveMs = 120,
            TotalSetupMs = 400,
            BufferingMs = 200,
            DurationMs = durationMs > 0 ? durationMs : _durationMs,
            Preset = "default",
            FirstPlay = _firstPlayInSession,
        };
        _firstPlayInSession = false;
        _batcher.Enqueue("BoomboxPlaybackSession", msg.ToByteArray());
    }

    void EmitHeadFileDownload(byte[] fileId, byte[] playbackId)
    {
        var msg = new HeadFileDownload
        {
            FileId = ByteString.CopyFrom(fileId),
            PlaybackId = ByteString.CopyFrom(playbackId),
            CdnDomain = "heads-fa-tls13.spotifycdn.com",
            HeadFileSize = 131072,
            HttpResult = 200,
            RequestType = "interactive",
        };
        _batcher.Enqueue("HeadFileDownload", msg.ToByteArray());
    }

    void EmitDownload(byte[] fileId, byte[] playbackId)
    {
        var msg = new Download
        {
            FileId = ByteString.CopyFrom(fileId),
            PlaybackId = ByteString.CopyFrom(playbackId),
            FileSize = long.MaxValue,
            BytesDownloaded = long.MaxValue,
            Realm = "music",
            CdnUriScheme = "https",
            CdnDomain = "audio-fa.scdn.co",
            RequestType = "interactive",
            Bitrate = _bitrateKbps > 0 ? _bitrateKbps * 1000 : 160_000,
        };
        _batcher.Enqueue("Download", msg.ToByteArray());
    }

    void EmitAudioRouteSegmentEnd(byte[] playbackId)
    {
        var msg = new AudioRouteSegmentEnd { PlaybackId = ByteString.CopyFrom(playbackId) };
        _batcher.Enqueue("AudioRouteSegmentEnd", msg.ToByteArray());
    }

    void EmitAdOpportunity(string playbackIdHex, string contentUri)
    {
        var msg = new AdOpportunityEvent
        {
            TriggerState = "PASS",
            ContentUri = contentUri,
            PlaybackId = playbackIdHex,
        };
        _batcher.Enqueue("AdOpportunityEvent", msg.ToByteArray());
    }

    string FormatAudioFormat()
    {
        if (!string.IsNullOrEmpty(_audioFormat)) return _audioFormat;
        int kbps = _bitrateKbps > 0 ? _bitrateKbps : 160;
        return $"Vorbis {kbps} kbps";
    }

    static string ParseContextKind(string? contextUri)
    {
        if (string.IsNullOrEmpty(contextUri)) return "unknown";
        var parts = contextUri.Split(':');
        return parts.Length >= 3 ? parts[1] : "unknown";
    }

    static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    void ResetTrack()
    {
        _ids = null;
        _contentUri = "";
        _msPlayed = 0;
        _fileId = null;
        _mediaId = null;
    }

    public ValueTask DisposeAsync() => _batcher.DisposeAsync();
}
