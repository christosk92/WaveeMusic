using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.SpotifyLive.Audio;
using M = Wavee.Protocol.Metadata;
using Af = Wavee.Protocol.Audiofiles;
using Wavee.Protocol.Storage;

namespace Wavee.SpotifyLive;

// ── Stage F — the live track resolver: Track -> AudioStreamHandle ─────────────────────────────────────────────────────
// File IDs are discovered via EXTENDED-METADATA, not the legacy /metadata/4/track/ route (which now returns a shell with
// an empty file[]). TRACK_V4 (kind 10) carries the Ogg/AAC file[] (+ alternative[] + original_audio); AUDIO_FILES (kind 5)
// on the spotify:audio: entity carries FLAC. We prefer lossless (FLAC) when the account returns it, else the Ogg ladder,
// falling through to an ALTERNATIVE track's file[] (using that alternative's gid for the audio key). Resolution is in
// scope; the decrypt/decode/output it feeds is the x64 host.
public sealed class LiveTrackResolver : ITrackResolver
{
    public sealed record CdnResolve(string[] Urls, TimeSpan Ttl);

    readonly ITransport _transport;
    readonly IAudioKeySource _keys;
    readonly Func<string, CancellationToken, Task<ByteString?>> _fetchTrackV4;
    readonly Func<string, CancellationToken, Task<ByteString?>>? _fetchAudioFilesV5;
    readonly Func<string, CancellationToken, Task<ByteString?>>? _fetchEpisodeV4;
    readonly bool _preferLossless;
    readonly Func<AudioQualityPreference>? _quality;
    readonly AudioFormatProbe? _probe;
    readonly WaveeLogger _log;
    readonly ConcurrentDictionary<string, Task<TrackMeta>> _metaCache = new();
    readonly Resource<string, CdnResolve> _cdnCache;

    public LiveTrackResolver(
        ITransport transport,
        IAudioKeySource keys,
        Func<string, CancellationToken, Task<ByteString?>> fetchTrackV4,
        Func<string, CancellationToken, Task<ByteString?>>? fetchAudioFilesV5 = null,
        Func<string, CancellationToken, Task<ByteString?>>? fetchEpisodeV4 = null,
        bool preferLossless = false,
        WaveeLogger log = default,
        AudioFormatProbe? probe = null,
        Func<AudioQualityPreference>? quality = null,
        Func<SessionContext>? ctx = null)
    {
        _transport = transport;
        _keys = keys;
        _fetchTrackV4 = fetchTrackV4;
        _fetchAudioFilesV5 = fetchAudioFilesV5;
        _fetchEpisodeV4 = fetchEpisodeV4;
        _preferLossless = preferLossless;
        _quality = quality;
        _probe = probe;
        _log = log;
        var session = ctx ?? (() => SessionContext.LoggedOut);
        _cdnCache = new Resource<string, CdnResolve>(FetchCdnAsync, new FreshnessPolicy.Immutable(), session,
            ttlOf: r => r.Ttl, maxEntries: 64, name: "audio.cdn", debugLog: log);
    }

    /// <summary>Drop the cached CDN mirrors for a file — call when the host/stream reports a body fetch failure.</summary>
    public void InvalidateCdn(string fileIdHex) => _cdnCache.Invalidate(fileIdHex);

    /// <summary>Per-entry CDN cache expiry — diagnostic / test visibility.</summary>
    public DateTime? PeekCdnExpiresAt(string fileIdHex) => _cdnCache.PeekExpiresAt(fileIdHex);

    // The effective preference: the live per-resolve delegate (the persisted setting) wins; the legacy bool maps to the
    // Lossless/VeryHigh320 rungs so existing call sites and tests keep their exact behavior.
    AudioQualityPreference Quality => _quality?.Invoke() ?? (_preferLossless ? AudioQualityPreference.Lossless : AudioQualityPreference.VeryHigh320);

    /// <summary>The fast half: extended-metadata → file select (Ogg or FLAC). No CDN, no key — so the head fetch (which
    /// needs no key) can start the moment this returns, in parallel with the body resolve. Coalesced + cached per track uri
    /// (file IDs are immutable); a failed resolve is dropped so it retries.</summary>
    public readonly record struct TrackMeta(byte[] FileId, string FileIdHex, byte[] FileGid, AudioFormat Fmt, long DurMs, string TrackUri, float NormalizationGainDb, string? ExternalUrl = null);

    public Task<TrackMeta> ResolveMetaAsync(Track track, CancellationToken ct = default)
    {
        // Cache the shared fetch task (CancellationToken.None so one caller's cancel can't poison the shared result).
        var task = _metaCache.GetOrAdd(track.Uri, _ => FetchMetaAsync(track));
        return AwaitAndDropOnFailure(track.Uri, task);
    }

    async Task<TrackMeta> AwaitAndDropOnFailure(string uri, Task<TrackMeta> task)
    {
        try { return await task.ConfigureAwait(false); }
        catch { _metaCache.TryRemove(new KeyValuePair<string, Task<TrackMeta>>(uri, task)); throw; }
    }

    async Task<TrackMeta> FetchMetaAsync(Track track)
    {
        if (EntityUri.KindOf(track.Uri) == EntityKind.Episode)
            return await FetchEpisodeMetaAsync(track).ConfigureAwait(false);

        var trackPayload = await _fetchTrackV4(track.Uri, CancellationToken.None).ConfigureAwait(false);
        if (trackPayload is null) throw new AudioPlaybackException(AudioKeyFailureReason.Restricted, "no TRACK_V4 extension for " + track.Uri);
        var t = M.Track.Parser.ParseFrom(trackPayload);

        var quality = Quality;

        // Lossless: AUDIO_FILES lives on the spotify:audio: entity derived from original_audio.uuid.
        Af.AudioFilesExtensionResponse? audioFiles = null;
        if ((quality == AudioQualityPreference.Lossless || _probe is not null) && _fetchAudioFilesV5 is { } fetchFlac && t.OriginalAudio?.Uuid is { Length: 16 } uuid)
        {
            var audioUri = "spotify:audio:" + Base62.Encode(uuid.Span);
            try
            {
                var flacPayload = await fetchFlac(audioUri, CancellationToken.None).ConfigureAwait(false);
                if (flacPayload is not null) audioFiles = Af.AudioFilesExtensionResponse.Parser.ParseFrom(flacPayload);
            }
            catch (Exception ex) { _log.Warn($"resolve {track.Uri}: AUDIO_FILES fetch failed ({ex.Message}) — Ogg only"); }
        }

        StartFormatProbe(track.Uri, t, audioFiles);

        var flac = quality == AudioQualityPreference.Lossless && audioFiles is not null ? SelectFlac(audioFiles) : null;
        var ogg = SelectOgg(t, quality);

        // Prefer FLAC when the account returned it; else the Ogg ladder (main track, then an alternative).
        if (flac is { } fl)
        {
            var gain = NormalizationGain(audioFiles!);
            var hex = Convert.ToHexStringLower(fl.fileId);
            _log.Debug($"resolve {track.Uri}: selected {fl.fmt} (lossless) file {hex} gain={gain:0.0}dB");
            return new TrackMeta(fl.fileId, hex, t.Gid.ToByteArray(), fl.fmt,   // FLAC AP-key uses the track's gid
                t.HasDuration ? t.Duration : track.DurationMs, track.Uri, gain);
        }
        if (ogg is { } og)
        {
            var hex = Convert.ToHexStringLower(og.fileId);
            _log.Debug($"resolve {track.Uri}: selected {og.fmt} file {hex}");
            return new TrackMeta(og.fileId, hex, og.gid, og.fmt, og.durMs > 0 ? og.durMs : track.DurationMs, track.Uri, 0f);
        }
        throw new AudioPlaybackException(AudioKeyFailureReason.Restricted, "no playable file (Ogg or FLAC, incl. alternatives)");
    }

    async Task<TrackMeta> FetchEpisodeMetaAsync(Track track)
    {
        if (_fetchEpisodeV4 is not { } fetchEp)
            throw new AudioPlaybackException(AudioKeyFailureReason.Restricted, "episode resolver not configured");
        var payload = await fetchEp(track.Uri, CancellationToken.None).ConfigureAwait(false);
        if (payload is null) throw new AudioPlaybackException(AudioKeyFailureReason.Restricted, "no EPISODE_V4 for " + track.Uri);
        var ep = M.Episode.Parser.ParseFrom(payload);
        long dur = ep.HasDuration ? ep.Duration : track.DurationMs;

        if (ep.HasExternalUrl && ep.ExternalUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            _log.Debug($"resolve {track.Uri}: external MP3 host={WaveeLogRedaction.UrlHost(ep.ExternalUrl)}");
            // Real gid-derived FileIdHex (not the "external" sentinel) so the host's stale-file guard has a stable key.
            var extGid = ep.Gid.ToByteArray();
            return new TrackMeta(extGid, Convert.ToHexStringLower(extGid), extGid, AudioFormat.Mp3, dur, track.Uri, 0f, ep.ExternalUrl);
        }

        var quality = Quality;
        var pick = SelectEpisodeAudio(ep, quality);
        if (pick is null)
            throw new AudioPlaybackException(AudioKeyFailureReason.Restricted, "no playable audio for episode " + track.Uri);
        var hex = Convert.ToHexStringLower(pick.Value.fileId);
        _log.Debug($"resolve {track.Uri}: episode {pick.Value.fmt} file {hex}");
        return new TrackMeta(pick.Value.fileId, hex, ep.Gid.ToByteArray(), pick.Value.fmt, dur, track.Uri, 0f);
    }

    static (byte[] fileId, AudioFormat fmt)? SelectEpisodeAudio(M.Episode episode, AudioQualityPreference quality)
    {
        if (episode.Audio.Count == 0) return null;
        int target = quality switch { AudioQualityPreference.Normal96 => 0, AudioQualityPreference.High160 => 1, _ => 2 };
        M.AudioFile? best = null;
        int bestScore = 0;
        AudioFormat bestFmt = AudioFormat.OggVorbis160;
        foreach (var f in episode.Audio)
        {
            if (f.FileId.Length == 0) continue;
            (int rung, AudioFormat fmt) = f.Format switch
            {
                M.AudioFile.Types.Format.OggVorbis96 => (0, AudioFormat.OggVorbis96),
                M.AudioFile.Types.Format.OggVorbis160 => (1, AudioFormat.OggVorbis160),
                M.AudioFile.Types.Format.OggVorbis320 => (2, AudioFormat.OggVorbis320),
                M.AudioFile.Types.Format.Mp3160 => (1, AudioFormat.Mp3),
                M.AudioFile.Types.Format.Mp3320 => (2, AudioFormat.Mp3),
                _ => (-1, AudioFormat.OggVorbis160),
            };
            if (rung < 0) continue;
            int score = rung == target ? 100 : rung < target ? 50 - (target - rung) : 10 - (rung - target);
            if (score > bestScore) { bestScore = score; best = f; bestFmt = fmt; }
        }
        if (best is null) return null;
        return (best.FileId.ToByteArray(), bestFmt);
    }

    void StartFormatProbe(string uri, M.Track track, Af.AudioFilesExtensionResponse? audioFiles)
    {
        var probe = _probe;
        if (probe is null) return;
        _ = Task.Run(async () =>
        {
            try { await probe.ProbeAsync(uri, track, audioFiles, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _log.Warn($"probe {uri}: failed ({ex.Message})"); }
        });
    }

    /// <summary>The slow half: storage-resolve (CDN, format-agnostic — keyed by fileId) + audio key (AP, else PlayPlay).
    /// Throws a typed <see cref="AudioPlaybackException"/> on any failure — never a silent empty handle.</summary>
    public async Task<AudioStreamHandle> ResolveBodyAsync(TrackMeta m, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(m.ExternalUrl))
            return new AudioStreamHandle(m.TrackUri, m.FileIdHex, m.ExternalUrl, default, AudioFormat.Mp3, m.DurMs, 0f,
                SourceKind: AudioSourceKind.ExternalPlain);

        var cdnLoaded = await _cdnCache.GetAsync(m.FileIdHex, ct).ConfigureAwait(false);
        if (!cdnLoaded.IsReady)
        {
            var reason = cdnLoaded.Error is { Length: > 0 } err && Enum.TryParse<AudioKeyFailureReason>(err, out var r)
                ? r : AudioKeyFailureReason.Network;
            throw new AudioPlaybackException(reason, cdnLoaded.Error);
        }
        var cdnUrls = cdnLoaded.Value!.Urls;
        var cdn = cdnUrls[0];

        _log.Debug($"storage-resolve {m.FileIdHex}: {cdnUrls.Length} cdn url(s); fetching key");
        var key = await _keys.GetKeyAsync(m.FileId, m.FileGid, ct).ConfigureAwait(false);   // typed throw on AP+PlayPlay failure
        var nativeSeed = _keys is IPlayPlayNativeSeedSource seedSource
            ? seedSource.GetNativeCdnSeed(m.FileIdHex)
            : default;
        return new AudioStreamHandle(m.TrackUri, m.FileIdHex, cdn, key, m.Fmt, m.DurMs, m.NormalizationGainDb, cdnUrls, NativeCdnSeed: nativeSeed);
    }

    async Task<CdnResolve> FetchCdnAsync(string fileIdHex, SessionContext ctx)
    {
        var sr = await _transport.Request(Channel.Spclient, "/storage-resolve/files/audio/interactive/" + fileIdHex, default, CancellationToken.None).ConfigureAwait(false);
        if (!sr.Ok)
            throw new InvalidOperationException(AudioKeyFailureReason.Network.ToString());
        var r = StorageResolveResponse.Parser.ParseFrom(sr.Body);
        if (r.Result == StorageResolveResponse.Types.Result.Restricted)
            throw new InvalidOperationException(AudioKeyFailureReason.Restricted.ToString());
        if (r.Cdnurl.Count == 0)
            throw new InvalidOperationException(AudioKeyFailureReason.Network.ToString());
        var urls = new string[r.Cdnurl.Count];
        for (int i = 0; i < r.Cdnurl.Count; i++) urls[i] = r.Cdnurl[i];
        var ttlSec = r.HasTtlSeconds && r.TtlSeconds > 0 ? r.TtlSeconds : 1800;
        var ttl = TimeSpan.FromSeconds(ttlSec * 0.8);
        return new CdnResolve(urls, ttl);
    }

    public async Task<AudioStreamHandle> ResolveAsync(Track track, CancellationToken ct = default)
    {
        try
        {
            var meta = await ResolveMetaAsync(track, ct).ConfigureAwait(false);
            return await ResolveBodyAsync(meta, ct).ConfigureAwait(false);
        }
        catch (AudioPlaybackException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Error("resolve failed for " + track.Uri + ": " + ex.Message, ex);
            throw new AudioPlaybackException(AudioKeyFailureReason.Network, ex.Message);
        }
    }

    // Spotify loudness normalization: gain = target(-14 LUFS) − track loudness, capped so the true peak stays ≤ −1 dBFS.
    static float NormalizationGain(Af.AudioFilesExtensionResponse resp)
    {
        var np = resp.DefaultFileNormalizationParams;
        if (np is null) return 0f;
        const float target = -14f;
        float gain = target - np.LoudnessDb;
        float peakHeadroom = -1f - np.TruePeakDb;
        return gain > peakHeadroom ? peakHeadroom : gain;
    }

    // FLAC from the AUDIO_FILES payload (24-bit preferred over 16-bit). gid = the track's (the FLAC is an alt encoding).
    internal static (byte[] fileId, AudioFormat fmt)? SelectFlac(Af.AudioFilesExtensionResponse resp)
    {
        Af.ExtendedAudioFile? best = null;
        int bestRank = 0;
        foreach (var ef in resp.Files)
        {
            var f = ef.File;
            if (f is null || f.FileId.Length == 0) continue;
            int rank = f.Format switch
            {
                M.AudioFile.Types.Format.FlacFlac24Bit => 2,
                M.AudioFile.Types.Format.FlacFlac => 1,
                _ => 0,
            };
            if (rank > bestRank) { bestRank = rank; best = ef; }
        }
        if (best is null) return null;
        return (best.File.FileId.ToByteArray(), bestRank == 2 ? AudioFormat.Flac24 : AudioFormat.Flac);
    }

    // Prefer the main track's Ogg files; if none, fall through to the FIRST alternative that has files (and its gid).
    internal static (byte[] fileId, byte[] gid, AudioFormat fmt, long durMs)? SelectOgg(M.Track track, AudioQualityPreference quality = AudioQualityPreference.VeryHigh320)
    {
        var pick = PickOgg(track.File, quality);
        if (pick is not null)
            return (pick.Value.fileId, track.Gid.ToByteArray(), pick.Value.fmt, track.HasDuration ? track.Duration : 0);

        foreach (var alt in track.Alternative)
        {
            var ap = PickOgg(alt.File, quality);
            if (ap is not null)
                return (ap.Value.fileId, alt.Gid.ToByteArray(), ap.Value.fmt,
                    alt.HasDuration ? alt.Duration : (track.HasDuration ? track.Duration : 0));
        }
        return null;
    }

    // Quality ladder: aim at the chosen rung (96/160/320); a missing rung falls back to the NEAREST available file,
    // preferring lower bitrates first (don't exceed the user's bandwidth choice), so something always plays.
    static (byte[] fileId, AudioFormat fmt)? PickOgg(IEnumerable<M.AudioFile> files, AudioQualityPreference quality)
    {
        int target = quality switch { AudioQualityPreference.Normal96 => 0, AudioQualityPreference.High160 => 1, _ => 2 };
        M.AudioFile? best = null;
        int bestScore = 0;
        AudioFormat bestFmt = AudioFormat.OggVorbis96;
        foreach (var f in files)
        {
            if (f.FileId.Length == 0) continue;
            (int rung, AudioFormat fmt) = f.Format switch
            {
                M.AudioFile.Types.Format.OggVorbis96 => (0, AudioFormat.OggVorbis96),
                M.AudioFile.Types.Format.OggVorbis160 => (1, AudioFormat.OggVorbis160),
                M.AudioFile.Types.Format.OggVorbis320 => (2, AudioFormat.OggVorbis320),
                _ => (-1, AudioFormat.OggVorbis96),
            };
            if (rung < 0) continue;
            int score = rung == target ? 100 : rung < target ? 50 - (target - rung) : 10 - (rung - target);
            if (score > bestScore) { bestScore = score; best = f; bestFmt = fmt; }
        }
        if (best is null) return null;
        return (best.FileId.ToByteArray(), bestFmt);
    }
}
