using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Wavee.Connect;
using Wavee.Connect.Commands;
using Wavee.Connect.Events;
using Wavee.Core.Audio;

namespace Wavee.Audio;

/// <summary>
/// Playback-event reporting for <see cref="PlaybackOrchestrator"/>. Drives
/// Spotify's Recently Played + play counts via gabo-receiver-service.
///
/// One <see cref="RawCoreStreamPlaybackEvent"/> per track end (natural finish,
/// skip, stop, replace by new PlayAsync). The underlying gabo schema also
/// supports per-track-start and per-segment events but those are nice-to-have
/// telemetry; only RawCoreStream is load-bearing for play history.
/// </summary>
public sealed partial class PlaybackOrchestrator
{
    private readonly EventService? _events;
    private readonly string _localDeviceId;

    // Guards the metrics group below. Hot paths snapshot+swap under lock,
    // then build/dispatch the event outside the lock — keep critical sections small.
    private readonly object _metricsLock = new();

    private string? _currentSessionId;          // 32-char hex; minted on context change
    private string? _currentSessionContextUri;  // tracks "did context change?"
    private PlaybackMetrics? _currentMetrics;   // active metrics for the playing track
    private TrackResolution? _currentResolution; // cached at PlayCurrentTrackAsync for end-of-track build
    private Stopwatch? _audioKeyStopwatch;      // measures audio-key fetch duration
    private PlayOrigin? _currentPlayOrigin;     // captured from PlayCommand for the active context

    // Last remote-command sender (set via RememberSender). Falls back to
    // _localDeviceId at dispatch time so the field is never null/empty —
    // librespot-java drops TrackTransitionEvent if lastCommandSentByDeviceId
    // is empty, so this fallback is load-bearing for self-initiated plays.
    private string? _lastCommandSenderDeviceId;

    // What the next track's ReasonStart should be. Updated in two places:
    //   • PlayAsync sets it to ClickRow / Remote based on origin.
    //   • DispatchTrackTransition mirrors the end reason (TrackDone⇒TrackDone, …).
    private PlaybackReason _nextStartReason = PlaybackReason.AppLoad;

    // Per-track segment bookkeeping. Each pause/resume edge creates a new
    // segment; we emit a RawCoreStreamSegment for each one and a final
    // isLast=true segment at track end. These pair with the closing
    // RawCoreStream so Spotify's stream_reporting FSM (per binary analysis
    // of `StreamReporter: FSM Transition`) sees a coherent open→...→close
    // event sequence rather than an orphaned final RawCoreStream.
    private long _segmentStartTimestampMs;
    private int _segmentStartPositionMs;
    private long _segmentSequenceId;

    // Per-play correlation token. Generated at track start, echoed in the
    // CorePlaybackCommandCorrelation event AND in RawCoreStream field 63.
    // 32 lowercase hex chars to match desktop wire (064_c.txt #63).
    private string? _currentCommandId;
    private long _currentTrackStartedAt;
    // BoomboxPlaybackSession.first_play=1 on the first track of a session;
    // 0 thereafter. Resets when EventService is constructed (per-session).
    private bool _firstPlayInSession = true;

    /// <summary>
    /// Records the most recent non-empty remote-command sender. Called from
    /// every remote-command lambda in <see cref="SubscribeToRemoteCommands"/>.
    /// </summary>
    private void RememberSender(string senderDeviceId)
    {
        if (!string.IsNullOrEmpty(senderDeviceId))
            Volatile.Write(ref _lastCommandSenderDeviceId, senderDeviceId);
    }

    /// <summary>
    /// Called from <see cref="PlayAsync"/> at the boundary of a new context.
    /// Flushes any in-flight track for the previous context, then bumps the
    /// session-context bookkeeping. Gabo's RawCoreStream doesn't need a
    /// per-context "session start" event — but we still need to flush the
    /// previous track and remember which context we're on so the next
    /// RawCoreStream picks up the right play_context.
    /// </summary>
    private void OnContextStarted(string contextUri, int contextSize, bool isLocalSender)
    {
        _ = contextSize; // retained for parity with the old NewSessionId schema
        if (_events == null) return;
        if (string.IsNullOrEmpty(contextUri)) return;

        if (_currentMetrics != null && _currentSessionContextUri != contextUri)
        {
            DispatchTrackTransition(
                isLocalSender ? PlaybackReason.ClickRow : PlaybackReason.Remote,
                _stateSubject.Value.PositionMs);
        }

        lock (_metricsLock)
        {
            _currentSessionContextUri = contextUri;
            // _currentSessionId stays for legacy read sites; not used by gabo.
            _currentSessionId ??= Guid.NewGuid().ToString("N");
        }
    }

    /// <summary>
    /// Called from <see cref="PlayCurrentTrackAsync"/> right after the track
    /// resolution is in hand. Mints a fresh 32-char hex playback ID, builds
    /// a <see cref="PlaybackMetrics"/> for the new track, and opens the first
    /// playback interval. Gabo doesn't have a separate per-track-start event
    /// for play history — RawCoreStream at end-of-track carries everything.
    /// </summary>
    private void OnTrackStarted(string trackUri, int positionMs, TrackResolution resolution)
    {
        if (_events == null) return;
        if (string.IsNullOrEmpty(trackUri)) return;

        // Only spotify:track:* URIs convert cleanly. Podcasts / local files
        // would produce a malformed hex ID and Spotify would silently drop
        // the event — skip metrics for them in v1.
        string trackHex;
        try
        {
            trackHex = SpotifyId.FromUri(trackUri).ToBase16();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Skipping metrics for non-track URI {Uri}", trackUri);
            ResetMetrics();
            return;
        }

        var playbackId = Guid.NewGuid().ToString("N");
        var origin = _currentPlayOrigin;
        var contextUri = _queue.ContextUri ?? _currentSessionContextUri ?? string.Empty;
        var commandId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var trackStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var metrics = new PlaybackMetrics(
            trackId: trackHex,
            playbackId: playbackId,
            contextUri: contextUri,
            featureVersion: origin?.FeatureVersion ?? string.Empty,
            referrerIdentifier: origin?.ReferrerIdentifier ?? string.Empty)
        {
            ReasonStart = _nextStartReason,
            SourceStart = _localDeviceId,
            SourceEnd = _localDeviceId,
        };
        metrics.StartInterval(positionMs);

        var sw = Stopwatch.StartNew();
        bool firstPlay;
        lock (_metricsLock)
        {
            _currentMetrics = metrics;
            _currentResolution = resolution;
            _audioKeyStopwatch = sw;
            _currentCommandId = commandId;
            _currentTrackStartedAt = trackStartedAt;
            _segmentStartTimestampMs = trackStartedAt;
            _segmentStartPositionMs = positionMs;
            _segmentSequenceId = 0;
            firstPlay = _firstPlayInSession;
            _firstPlayInSession = false;
        }

        // Track-start batch: one POST containing AudioSessionEvent(open) +
        // CorePlaybackCommandCorrelation + BoomboxPlaybackSession. Mirrors
        // the desktop client's 062_c.txt batch shape (minus ContentIntegrity
        // / AudioResolve / AudioFileSelection / AudioRouteSegmentEnd /
        // AudioDriverInfo, which require binary attestation or audio-driver
        // hooks Wavee doesn't have).
        var playbackIdBytes = Convert.FromHexString(playbackId);
        var batch = new List<IPlaybackEvent>(3)
        {
            AudioSessionPlaybackEvent.Open(playbackId, _currentSessionContextUri),
            new CorePlaybackCommandCorrelationEvent(playbackIdBytes, commandId),
            new BoomboxPlaybackSessionEvent(
                playbackId: playbackIdBytes,
                audioKeyMs: 0,
                resolveMs: 0,
                totalSetupMs: 0,
                bufferingMs: 0,
                durationMs: resolution.DurationMs,
                firstPlay: firstPlay),
        };

        try { _events.SendEventBatch(batch); }
        catch (Exception ex) { _logger?.LogDebug(ex, "Track-start batch dispatch failed"); }
    }

    private void OnVideoTrackStarted(SpotifyVideoPlaybackTarget target, int positionMs)
    {
        if (_events == null) return;
        if (string.IsNullOrEmpty(target.VideoTrackUri)) return;

        string trackHex;
        try
        {
            trackHex = SpotifyId.FromUri(target.VideoTrackUri).ToBase16();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Skipping video metrics for non-track URI {Uri}", target.VideoTrackUri);
            ResetMetrics();
            return;
        }

        var playbackId = Guid.NewGuid().ToString("N");
        var origin = _currentPlayOrigin;
        var contextUri = _queue.ContextUri ?? _currentSessionContextUri ?? string.Empty;
        var commandId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var trackStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var metrics = new PlaybackMetrics(
            trackId: trackHex,
            playbackId: playbackId,
            contextUri: contextUri,
            featureVersion: origin?.FeatureVersion ?? string.Empty,
            referrerIdentifier: origin?.ReferrerIdentifier ?? string.Empty,
            mediaType: "video")
        {
            ReasonStart = _nextStartReason,
            SourceStart = _localDeviceId,
            SourceEnd = _localDeviceId,
            Player = new PlayerMetrics
            {
                Duration = (int)Math.Min(target.DurationMs, int.MaxValue),
                Encoding = "video",
                Transition = "none",
            }
        };
        metrics.StartInterval(positionMs);

        bool firstPlay;
        lock (_metricsLock)
        {
            _currentMetrics = metrics;
            _currentResolution = null;
            _audioKeyStopwatch = null;
            _currentCommandId = commandId;
            _currentTrackStartedAt = trackStartedAt;
            _segmentStartTimestampMs = trackStartedAt;
            _segmentStartPositionMs = positionMs;
            _segmentSequenceId = 0;
            firstPlay = _firstPlayInSession;
            _firstPlayInSession = false;
        }

        var playbackIdBytes = Convert.FromHexString(playbackId);
        var batch = new List<IPlaybackEvent>(3)
        {
            AudioSessionPlaybackEvent.Open(playbackId, _currentSessionContextUri),
            new CorePlaybackCommandCorrelationEvent(playbackIdBytes, commandId),
            new BoomboxPlaybackSessionEvent(
                playbackId: playbackIdBytes,
                audioKeyMs: 0,
                resolveMs: 0,
                totalSetupMs: 0,
                bufferingMs: 0,
                durationMs: target.DurationMs,
                firstPlay: firstPlay),
        };

        try { _events.SendEventBatch(batch); }
        catch (Exception ex) { _logger?.LogDebug(ex, "Video track-start batch dispatch failed"); }
    }

    /// <summary>
    /// Called from <see cref="OnProxyStateChanged"/> on every state push.
    /// Detects the pause edge (close interval, emit segment + Pause event)
    /// and resume edge (open new interval, emit Resume event). Seek-during-play
    /// is not split in v1 — Spotify accepts the resulting interval span unchanged.
    /// </summary>
    private void OnIntervalEdge(LocalPlaybackState prev, LocalPlaybackState next)
    {
        if (_currentMetrics == null || _events == null) return;

        // Pause edge: was playing, now paused. End current segment, emit
        // RawCoreStreamSegment + AudioSessionEvent.Pause.
        if (prev.IsPlaying && !next.IsPlaying && next.IsPaused)
        {
            var endPos = (int)Math.Min(next.PositionMs, int.MaxValue);
            lock (_metricsLock)
                _currentMetrics?.EndInterval(endPos);
            EmitSegment(endPos, isPause: true, isLast: false, reasonEnd: "pause");
            try { _events.SendEvent(AudioSessionPlaybackEvent.Pause(_currentMetrics!.PlaybackId)); }
            catch (Exception ex) { _logger?.LogDebug(ex, "AudioSession pause dispatch failed"); }
            return;
        }

        // Resume edge: was paused/buffering, now playing — and it's still the
        // same track (track-change handling opens its own interval in OnTrackStarted).
        if (!prev.IsPlaying && next.IsPlaying && prev.TrackUri == next.TrackUri)
        {
            var startPos = (int)Math.Min(next.PositionMs, int.MaxValue);
            lock (_metricsLock)
            {
                _currentMetrics?.StartInterval(startPos);
                _segmentStartTimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _segmentStartPositionMs = startPos;
            }
            try { _events.SendEvent(AudioSessionPlaybackEvent.Resume(_currentMetrics!.PlaybackId)); }
            catch (Exception ex) { _logger?.LogDebug(ex, "AudioSession resume dispatch failed"); }
        }
    }

    /// <summary>
    /// Emits a <see cref="RawCoreStreamSegmentPlaybackEvent"/> for the segment
    /// that just ended (start of segment → <paramref name="endPosMs"/>). Bumps
    /// the per-track sequence id used by stream_reporting's FSM to correlate
    /// segments back to the final RawCoreStream. Used for mid-track pause
    /// edges; the final-segment-at-track-end goes through <see cref="BuildSegment"/>
    /// + the track-end batch instead.
    /// </summary>
    private void EmitSegment(int endPosMs, bool isPause, bool isLast, string reasonEnd)
    {
        var metrics = _currentMetrics;
        if (_events == null || metrics == null) return;

        try { _events.SendEvent(BuildSegment(metrics, endPosMs, isPause, isLast, reasonEnd)); }
        catch (Exception ex) { _logger?.LogDebug(ex, "RawCoreStreamSegment dispatch failed"); }
    }

    /// <summary>
    /// Builds (but does not dispatch) the next RawCoreStreamSegment event from
    /// the supplied <paramref name="metrics"/> + end position. Bumps
    /// <see cref="_segmentSequenceId"/> as a side-effect. Pass an explicit
    /// <see cref="PlaybackMetrics"/> so this works in <see cref="DispatchTrackTransition"/>
    /// after <c>_currentMetrics</c> has already been swapped to null.
    /// </summary>
    private IPlaybackEvent BuildSegment(PlaybackMetrics metrics, int endPosMs, bool isPause, bool isLast, string reasonEnd)
    {
        long startTs, sequenceId, startPos;
        string playbackId = metrics.PlaybackId;
        string trackHex = metrics.TrackId;
        string contextUri = metrics.ContextUri ?? string.Empty;
        lock (_metricsLock)
        {
            startTs = _segmentStartTimestampMs;
            startPos = _segmentStartPositionMs;
            _segmentSequenceId++;
            sequenceId = _segmentSequenceId;
        }

        var trackUri = string.IsNullOrEmpty(trackHex)
            ? string.Empty
            : "spotify:track:" + Wavee.Core.Audio.SpotifyId.FromBase16(trackHex, Wavee.Core.Audio.SpotifyIdType.Track).ToBase62();

        return new RawCoreStreamSegmentPlaybackEvent(
            playbackIdHex: playbackId,
            trackUri: trackUri,
            contextUri: contextUri,
            startPositionMs: startPos,
            endPositionMs: endPosMs,
            startTimestampMs: startTs,
            endTimestampMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            reasonStart: _nextStartReason.ToEventValue(),
            reasonEnd: reasonEnd,
            isPause: isPause,
            isSeek: false,
            isLast: isLast,
            sequenceId: sequenceId,
            mediaType: metrics.MediaType);
    }

    /// <summary>
    /// Parses a Spotify URI like "spotify:playlist:..." into its kind segment
    /// ("playlist"). Returns "unknown" for empty / malformed URIs.
    /// </summary>
    private static string ParseContextKind(string? contextUri)
    {
        if (string.IsNullOrEmpty(contextUri)) return "unknown";
        var parts = contextUri.Split(':');
        return parts.Length >= 3 ? parts[1] : "unknown";
    }

    /// <summary>
    /// Returns the next queued track's base62 id, or empty string if there is
    /// no next track / it's not a spotify:track URI. Wire field 59 of
    /// RawCoreStream — desktop sends this so Spotify can attribute the
    /// auto-advance flow.
    /// </summary>
    private string TryGetNextTrackBase62()
    {
        try
        {
            var nextTracks = _queue.GetNextTracks();
            if (nextTracks.Count == 0) return string.Empty;
            var nextUri = nextTracks[0].Uri;
            if (!nextUri.StartsWith("spotify:track:", StringComparison.Ordinal)) return string.Empty;
            return Wavee.Core.Audio.SpotifyId.FromUri(nextUri).ToBase62();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Builds and dispatches a <see cref="RawCoreStreamPlaybackEvent"/> for
    /// the currently-tracked playback, then clears the in-flight metrics.
    /// This is the event Spotify ingests for Recently Played + play counts.
    /// No-op if no metrics are active (e.g. Connect disabled, resolve faulted,
    /// or already dispatched). Updates <see cref="_nextStartReason"/> so the
    /// next track's ReasonStart mirrors this end.
    /// </summary>
    private void DispatchTrackTransition(PlaybackReason reasonEnd, long endPositionMs)
    {
        if (_events == null) return;

        PlaybackMetrics? toSend;
        TrackResolution? res;
        Stopwatch? sw;
        lock (_metricsLock)
        {
            toSend = _currentMetrics;
            res = _currentResolution;
            sw = _audioKeyStopwatch;
            _currentMetrics = null;
            _currentResolution = null;
            _audioKeyStopwatch = null;
        }
        if (toSend == null) return;

        sw?.Stop();
        toSend.EndInterval((int)Math.Min(endPositionMs, int.MaxValue));
        toSend.ReasonEnd = reasonEnd;
        toSend.Player ??= BuildPlayerMetrics(res, sw);

        var sender = Volatile.Read(ref _lastCommandSenderDeviceId);
        if (string.IsNullOrEmpty(sender)) sender = _localDeviceId;

        // Reconstruct the track URI from the metrics' hex track id so
        // RawCoreStream.content_uri carries the spotify:track: form.
        var trackUri = string.IsNullOrEmpty(toSend.TrackId)
            ? string.Empty
            : "spotify:track:" + Wavee.Core.Audio.SpotifyId.FromBase16(toSend.TrackId, Wavee.Core.Audio.SpotifyIdType.Track).ToBase62();

        var isCachedHit = res?.LocalCacheFileId != null;
        var contextKind = ParseContextKind(toSend.ContextUri);
        var commandId = _currentCommandId
            ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var nextTrackBase62 = TryGetNextTrackBase62();

        var fileIdHex = res?.SpotifyFileId ?? string.Empty;
        var fileIdBytes = string.IsNullOrEmpty(fileIdHex)
            ? Array.Empty<byte>()
            : SafeFromHex(fileIdHex);
        var playbackIdBytes = SafeFromHex(toSend.PlaybackId);
        var playerMetrics = toSend.Player ?? BuildPlayerMetrics(res, sw);

        // Track-end batch: one POST mirroring desktop's 032_c.txt shape.
        // Order: final RawCoreStreamSegment → AudioSessionEvent(close) →
        // HeadFileDownload → Download → RawCoreStream.
        var batch = new List<IPlaybackEvent>(5);

        batch.Add(BuildSegment(
            metrics: toSend,
            endPosMs: (int)Math.Min(endPositionMs, int.MaxValue),
            isPause: false,
            isLast: true,
            reasonEnd: reasonEnd.ToEventValue()));

        batch.Add(AudioSessionPlaybackEvent.Close(toSend.PlaybackId, reasonEnd.ToEventValue(), toSend.ContextUri));

        // HeadFileDownload + Download — only if we actually had a file id.
        // Latency placeholders match the desktop capture range; real per-fetch
        // instrumentation is a future improvement (TODO).
        if (fileIdBytes.Length > 0 && playbackIdBytes.Length > 0)
        {
            batch.Add(new HeadFileDownloadPlaybackEvent(
                fileId: fileIdBytes,
                playbackId: playbackIdBytes,
                httpLatencyMs: 220,
                http64kLatencyMs: 250,
                totalTimeMs: 270,
                httpResult: 200));

            batch.Add(new DownloadPlaybackEvent(
                fileId: fileIdBytes,
                playbackId: playbackIdBytes,
                fileSize: playerMetrics.Size,
                bytesDownloaded: playerMetrics.Size,
                cdnDomain: "audio-fa.scdn.co",
                requestType: "interactive",
                bitrate: playerMetrics.Bitrate,
                httpLatencyMs: 200,
                totalTimeMs: 250));
        }

        batch.Add(new RawCoreStreamPlaybackEvent(
            metrics: toSend,
            trackUri: trackUri,
            connectControllerDeviceId: sender,
            isCachedHit: isCachedHit,
            contextKind: contextKind,
            commandIdHex: commandId,
            nextTrackBase62: nextTrackBase62));

        // Anti-ripping attestation. Server's play-history pipeline cross-
        // references RawCoreStream.playback_id against a ContentIntegrity
        // event with ripping flags clear — without this the play is dropped
        // before reaching Recently Played. Same playback_id, same batch.
        batch.Add(new ContentIntegrityPlaybackEvent(
            playbackIdHex: toSend.PlaybackId ?? string.Empty));

        try { _events.SendEventBatch(batch); }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Track-end batch dispatch failed (reasonEnd={Reason})", reasonEnd);
        }

        // Mirror end → start so the next track's ReasonStart is accurate
        // for the auto-advance / skip / etc. path.
        _nextStartReason = reasonEnd switch
        {
            PlaybackReason.TrackDone => PlaybackReason.TrackDone,
            PlaybackReason.ForwardBtn => PlaybackReason.ForwardBtn,
            PlaybackReason.BackBtn => PlaybackReason.BackBtn,
            _ => PlaybackReason.ClickRow,
        };
    }

    /// <summary>Hex → bytes with empty fallback on malformed input.</summary>
    private static byte[] SafeFromHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return Array.Empty<byte>();
        try { return Convert.FromHexString(hex); }
        catch { return Array.Empty<byte>(); }
    }

    private void ResetMetrics()
    {
        lock (_metricsLock)
        {
            _currentMetrics = null;
            _currentResolution = null;
            _audioKeyStopwatch?.Stop();
            _audioKeyStopwatch = null;
        }
    }

    /// <summary>
    /// Packs a <see cref="TrackResolution"/> + audio-key stopwatch into a
    /// <see cref="PlayerMetrics"/>. Always returns a non-null instance with a
    /// non-null <see cref="ContentMetrics"/> so <see cref="TrackTransitionEvent.Build"/>
    /// never throws on missing player data.
    /// </summary>
    private static PlayerMetrics BuildPlayerMetrics(TrackResolution? r, Stopwatch? sw)
    {
        var keyMs = (int)(sw?.ElapsedMilliseconds ?? 0);

        long fileSize = 0;
        if (r?.FileSizeTask is { IsCompletedSuccessfully: true } sizeTask)
        {
            try { fileSize = sizeTask.Result; }
            catch { fileSize = 0; }
        }

        var fileId = r?.SpotifyFileId;

        return new PlayerMetrics
        {
            ContentMetrics = ContentMetrics.Create(
                fileId: fileId,
                wasCached: false,
                audioKeyTimeMs: keyMs),
            Bitrate = (r?.BitrateKbps ?? 0) * 1000,
            Duration = (int)Math.Min(r?.DurationMs ?? 0, int.MaxValue),
            Encoding = string.IsNullOrEmpty(r?.Codec) ? "vorbis" : r!.Codec,
            Size = (int)Math.Min(fileSize, int.MaxValue),
            DecodedLength = (int)Math.Min(fileSize, int.MaxValue),
            SampleRate = 44100f,
            FadeOverlap = 0,
            Transition = "none",
            DecryptTime = 0,
        };
    }
}
