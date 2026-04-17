using Microsoft.Extensions.Logging;
using Wavee.Core.Audio;
using Wavee.Core.Http;
using Wavee.Core.Session;
using Wavee.Core.Storage;
using Wavee.Playback.Contracts;
using Wavee.Protocol.Storage;
using Wavee.Protocol.Metadata;

namespace Wavee.Audio;

/// <summary>
/// Resolves a Spotify track/episode URI into everything AudioHost needs to play it:
/// CDN URL, audio decryption key, codec, normalization data, and metadata.
/// Extracts logic from SpotifyTrackSource without creating any audio streams.
/// </summary>
public sealed class TrackResolver
{
    // CDN token URLs expire after ~1 hour on Spotify's side; cache them for 30 minutes
    // to avoid re-fetching on rapid replays while staying well within expiry.
    private static readonly TimeSpan CdnUrlCacheTtl = TimeSpan.FromMinutes(30);

    private readonly Session _session;
    private readonly SpClient _spClient;
    private readonly HeadFileClient _headFileClient;
    private readonly IExtendedMetadataClient? _extendedMetadataClient;
    private readonly ICacheService? _cacheService;
    private readonly HttpClient _httpClient;
    private AudioQuality _preferredQuality;
    private readonly ILogger? _logger;

    /// <summary>
    /// Directory where AudioHost persists fully downloaded tracks.
    /// When set, TrackResolver checks here before making CDN and head-file requests —
    /// if a track is already fully cached, both network calls are skipped entirely.
    /// </summary>
    private readonly string? _audioCacheDirectory;

    public TrackResolver(
        Session session,
        SpClient spClient,
        HeadFileClient headFileClient,
        HttpClient httpClient,
        AudioQuality preferredQuality = AudioQuality.VeryHigh,
        IExtendedMetadataClient? extendedMetadataClient = null,
        ICacheService? cacheService = null,
        ILogger? logger = null,
        string? audioCacheDirectory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _spClient = spClient ?? throw new ArgumentNullException(nameof(spClient));
        _headFileClient = headFileClient ?? throw new ArgumentNullException(nameof(headFileClient));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _preferredQuality = preferredQuality;
        _extendedMetadataClient = extendedMetadataClient;
        _cacheService = cacheService;
        _logger = logger;
        _audioCacheDirectory = audioCacheDirectory;
    }

    /// <summary>
    /// Fetches head data from cache or network. Head data is safe to cache permanently
    /// — the CDN serves it with max-age=315360000 (10 years) and file IDs never change.
    /// </summary>
    private async Task<byte[]?> GetHeadDataAsync(FileId fileId, CancellationToken ct)
    {
        if (_cacheService != null)
        {
            var cached = await _cacheService.GetHeadDataAsync(fileId, ct);
            if (cached != null)
            {
                _logger?.LogDebug("Head data cache HIT for {FileId}", fileId.ToBase16());
                return cached;
            }
        }

        var data = await _headFileClient.TryFetchHeadAsync(fileId, ct);

        if (data != null && _cacheService != null)
            await _cacheService.SetHeadDataAsync(fileId, data, ct);

        return data;
    }

    /// <summary>
    /// Resolves CDN URL from cache or spclient. CDN tokens expire in ~1 hour so we
    /// cache with a 30-minute TTL to avoid refetching on rapid replays.
    /// </summary>
    private async Task<StorageResolveResponse> GetCdnUrlAsync(FileId fileId, CancellationToken ct)
    {
        if (_cacheService != null)
        {
            var cached = await _cacheService.GetCdnUrlAsync(fileId, ct);
            if (cached != null)
            {
                _logger?.LogDebug("CDN URL cache HIT for {FileId}", fileId.ToBase16());
                // Re-wrap as a StorageResolveResponse so callers don't need to change
                var cachedResponse = new StorageResolveResponse();
                cachedResponse.Cdnurl.Add(cached.Url);
                return cachedResponse;
            }
        }

        var response = await _spClient.ResolveAudioStorageAsync(fileId, ct);

        if (response.Cdnurl.Count > 0 && _cacheService != null)
            await _cacheService.SetCdnUrlAsync(fileId, response.Cdnurl[0], CdnUrlCacheTtl, ct);

        return response;
    }

    public void SetPreferredQuality(AudioQuality quality) => _preferredQuality = quality;

    /// <summary>
    /// Warms the caches (head data, AudioKey, CDN URL) for an upcoming track so the
    /// real <see cref="ResolveAsync"/> call at track-start is a pure cache hit.
    /// Call this while the *current* track is still playing (e.g. past 50% or with
    /// ≤20 s remaining). Safe to call repeatedly — every cache layer already
    /// deduplicates by FileId. All exceptions are swallowed: prefetch failure must
    /// never bubble up and break the currently playing track.
    /// </summary>
    public async Task PrefetchAsync(string uri, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(uri)) return;
        // Episodes fetch different metadata; keep prefetch scope to tracks for now.
        if (uri.StartsWith("spotify:episode:", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var trackId = SpotifyId.FromUri(uri);
            var track = await FetchTrackMetadataAsync(uri, trackId, ct).ConfigureAwait(false);

            var (selectedFile, effectiveTrack) = await SelectAudioFileAsync(track, _preferredQuality, ct).ConfigureAwait(false);
            if (selectedFile == null || effectiveTrack == null) return;

            var effectiveTrackId = effectiveTrack.Gid is { Length: > 0 }
                ? SpotifyId.FromRaw(effectiveTrack.Gid.Span, SpotifyIdType.Track)
                : trackId;

            var fileId = FileId.FromBytes(selectedFile.FileId.Span);
            var fileIdHex = fileId.ToBase16();

            // If the encrypted file is already on disk, skip CDN + head entirely
            // and only warm the AudioKey (decryption needs it at playback time).
            if (_audioCacheDirectory != null && AudioFileCache.IsCached(_audioCacheDirectory, fileIdHex))
            {
                await _session.AudioKeys.RequestAudioKeyAsync(effectiveTrackId, fileId, ct).ConfigureAwait(false);
                _logger?.LogDebug("Prefetch: audio cached on disk, warmed AudioKey only for {Uri}", uri);
                return;
            }

            // Fire all three in parallel — each writes to its own cache on completion.
            // The existing GetHeadDataAsync / RequestAudioKeyAsync / GetCdnUrlAsync
            // methods all short-circuit on cache hit, so this is a no-op if already warm.
            var headTask = GetHeadDataAsync(fileId, ct);
            var keyTask = _session.AudioKeys.RequestAudioKeyAsync(effectiveTrackId, fileId, ct);
            var cdnTask = GetCdnUrlAsync(fileId, ct);

            await Task.WhenAll(headTask, keyTask, cdnTask).ConfigureAwait(false);
            _logger?.LogInformation("Prefetched next track: {Uri} (fileId={FileId})", uri, fileIdHex);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is fine — prefetch is opportunistic.
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Prefetch failed for {Uri} (non-fatal, real resolve will retry)", uri);
        }
    }

    /// <summary>
    /// Resolves a track URI to a fully resolved track ready for AudioHost.
    /// </summary>
    public async Task<ResolvedTrack> ResolveAsync(string uri, CancellationToken ct = default)
    {
        if (uri.StartsWith("spotify:episode:", StringComparison.OrdinalIgnoreCase))
            return await ResolveEpisodeAsync(uri, ct);

        return await ResolveTrackAsync(uri, ct);
    }

    /// <summary>
    /// Resolves a track with deferred CDN — returns head data immediately,
    /// CDN URL + audio key as background tasks.
    /// </summary>
    public async Task<TrackResolution> ResolveWithHeadAsync(string uri, CancellationToken ct = default)
    {
        _logger?.LogInformation("Resolving track (with head) {Uri} at quality {Quality}", uri, _preferredQuality);

        var trackId = SpotifyId.FromUri(uri);
        var track = await FetchTrackMetadataAsync(uri, trackId, ct);

        var (selectedFile, effectiveTrack) = await SelectAudioFileAsync(track, _preferredQuality, ct);
        if (selectedFile == null || effectiveTrack == null)
            throw new InvalidOperationException($"No suitable audio file found for track {uri}");

        var effectiveTrackId = effectiveTrack.Gid is { Length: > 0 }
            ? SpotifyId.FromRaw(effectiveTrack.Gid.Span, SpotifyIdType.Track)
            : trackId;

        var fileId = FileId.FromBytes(selectedFile.FileId.Span);
        var fileIdHex = fileId.ToBase16();
        var audioFormat = MapToAudioFileFormat(selectedFile.Format);

        // ── Cache short-circuit ────────────────────────────────────────────────────
        // If the full encrypted audio file is already on disk, skip both the CDN
        // storage-resolve call AND the head-file fetch. We still need the audio key
        // (decryption happens at playback time regardless of source).
        if (_audioCacheDirectory != null && AudioFileCache.IsCached(_audioCacheDirectory, fileIdHex))
        {
            _logger?.LogInformation("Cache HIT for {FileId} — skipping CDN and head fetch", fileIdHex);

            var cachedFileSize = AudioFileCache.GetCachedFileSize(_audioCacheDirectory, fileIdHex);
            var keyTaskCached = _session.AudioKeys.RequestAudioKeyAsync(effectiveTrackId, fileId, ct);
            var metadata = BuildMetadataDto(uri, track, NormalizationData.Default);

            return new TrackResolution
            {
                TrackUri = uri,
                Codec = GetCodecName(audioFormat),
                BitrateKbps = audioFormat.GetBitrate(),
                HeadData = null,
                Normalization = NormalizationData.Default,
                Metadata = metadata,
                DurationMs = track.Duration,
                AudioKeyTask = keyTaskCached,
                CdnUrlTask = Task.FromResult(""),  // not used
                FileSizeTask = Task.FromResult(cachedFileSize),
                SpotifyFileId = fileIdHex,
                LocalCacheFileId = fileIdHex,
            };
        }

        // ── Normal (CDN) path ──────────────────────────────────────────────────────

        // Start all three in parallel — head file awaited first for instant start
        var headTask = GetHeadDataAsync(fileId, ct);
        var keyTask = _session.AudioKeys.RequestAudioKeyAsync(effectiveTrackId, fileId, ct);
        var cdnTask = GetCdnUrlAsync(fileId, ct);

        // Wait only for head file
        var headData = await headTask;

        var normalization = headData != null && headData.Length > NormalizationData.FileOffset + NormalizationData.Size
            ? ReadNormalizationFromHeadData(headData)
            : NormalizationData.Default;

        var trackMetadata = BuildMetadataDto(uri, track, normalization);

        // CDN URL and audio key complete asynchronously
        var cdnUrlTask = Task.Run(async () =>
        {
            var response = await cdnTask;
            return response.Cdnurl.Count > 0 ? response.Cdnurl[0] : throw new InvalidOperationException("No CDN URLs");
        });

        var fileSizeTask = Task.Run(async () =>
        {
            var cdnUrl = await cdnUrlTask;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, cdnUrl);
                using var resp = await _httpClient.SendAsync(req, ct);
                return resp.Content.Headers.ContentLength ?? 8 * 1024 * 1024;
            }
            catch { return 8L * 1024 * 1024; }
        });

        return new TrackResolution
        {
            TrackUri = uri,
            Codec = GetCodecName(audioFormat),
            BitrateKbps = audioFormat.GetBitrate(),
            HeadData = headData,
            Normalization = normalization,
            Metadata = trackMetadata,
            DurationMs = track.Duration,
            AudioKeyTask = keyTask,
            CdnUrlTask = cdnUrlTask,
            FileSizeTask = fileSizeTask,
            SpotifyFileId = fileIdHex,
        };
    }

    private async Task<ResolvedTrack> ResolveTrackAsync(string uri, CancellationToken ct)
    {
        _logger?.LogInformation("Resolving track {Uri} at quality {Quality}", uri, _preferredQuality);

        // 1. Parse URI
        var trackId = SpotifyId.FromUri(uri);

        // 2. Fetch metadata
        var track = await FetchTrackMetadataAsync(uri, trackId, ct);

        // 3. Select audio file
        var (selectedFile, effectiveTrack) = await SelectAudioFileAsync(track, _preferredQuality, ct);
        if (selectedFile == null || effectiveTrack == null)
            throw new InvalidOperationException($"No suitable audio file found for track {uri}");

        var effectiveTrackId = effectiveTrack.Gid is { Length: > 0 }
            ? SpotifyId.FromRaw(effectiveTrack.Gid.Span, SpotifyIdType.Track)
            : trackId;

        var fileId = FileId.FromBytes(selectedFile.FileId.Span);
        var audioFormat = MapToAudioFileFormat(selectedFile.Format);

        // 4. Parallel fetches: head file + audio key + CDN URL
        var headTask = GetHeadDataAsync(fileId, ct);
        var keyTask = _session.AudioKeys.RequestAudioKeyAsync(effectiveTrackId, fileId, ct);
        var cdnTask = GetCdnUrlAsync(fileId, ct);

        // Wait for head (for normalization) and the other two
        var headData = await headTask;
        await Task.WhenAll(keyTask, cdnTask);

        var audioKey = await keyTask;
        var cdnResponse = await cdnTask;

        if (cdnResponse.Cdnurl.Count == 0)
            throw new InvalidOperationException($"No CDN URLs returned for file {fileId.ToBase16()}");

        var cdnUrl = cdnResponse.Cdnurl[0];

        // 5. Read normalization from head data
        var normalization = headData != null && headData.Length > NormalizationData.FileOffset + NormalizationData.Size
            ? ReadNormalizationFromHeadData(headData)
            : NormalizationData.Default;

        // 6. Build metadata DTO
        var metadata = BuildMetadataDto(uri, track, normalization);

        _logger?.LogInformation("Resolved: {Title} by {Artist} [{Format}] CDN ready",
            metadata.Title, metadata.Artist, audioFormat);

        return new ResolvedTrack
        {
            TrackUri = uri,
            CdnUrl = cdnUrl,
            AudioKey = audioKey,
            FileId = fileId.ToBase16(),
            Codec = GetCodecName(audioFormat),
            BitrateKbps = audioFormat.GetBitrate(),
            NormalizationGain = normalization.TrackGainDb,
            NormalizationPeak = normalization.TrackPeak,
            DurationMs = track.Duration,
            Metadata = metadata
        };
    }

    private async Task<ResolvedTrack> ResolveEpisodeAsync(string uri, CancellationToken ct)
    {
        _logger?.LogInformation("Resolving episode {Uri}", uri);

        var episodeId = SpotifyId.FromUri(uri);
        var metadataBytes = await _spClient.GetEpisodeMetadataAsync(episodeId.ToBase62(), ct);
        var episode = Episode.Parser.ParseFrom(metadataBytes);

        // Select audio file from episode
        if (episode.Audio.Count == 0)
            throw new InvalidOperationException($"No audio files for episode {uri}");

        var selectedFile = SelectAudioFileFromEpisode(episode, _preferredQuality);
        if (selectedFile == null)
            throw new InvalidOperationException($"No suitable audio file for episode {uri}");

        var fileId = FileId.FromBytes(selectedFile.FileId.Span);
        var audioFormat = MapToAudioFileFormat(selectedFile.Format);

        // Parallel fetches
        var headTask = GetHeadDataAsync(fileId, ct);
        var keyTask = Task.Run(() => _session.AudioKeys.RequestAudioKeyAsync(episodeId, fileId, ct));
        var cdnTask = Task.Run(() => GetCdnUrlAsync(fileId, ct));

        var headData = await headTask;
        await Task.WhenAll(keyTask, cdnTask);

        var audioKey = await keyTask;
        var cdnResponse = await cdnTask;

        if (cdnResponse.Cdnurl.Count == 0)
            throw new InvalidOperationException($"No CDN URLs for episode file {fileId.ToBase16()}");

        var normalization = headData != null && headData.Length > NormalizationData.FileOffset + NormalizationData.Size
            ? ReadNormalizationFromHeadData(headData)
            : NormalizationData.Default;

        return new ResolvedTrack
        {
            TrackUri = uri,
            CdnUrl = cdnResponse.Cdnurl[0],
            AudioKey = audioKey,
            FileId = fileId.ToBase16(),
            Codec = GetCodecName(audioFormat),
            BitrateKbps = audioFormat.GetBitrate(),
            NormalizationGain = normalization.TrackGainDb,
            NormalizationPeak = normalization.TrackPeak,
            DurationMs = episode.Duration,
            Metadata = new TrackMetadataDto
            {
                Title = episode.Name,
                Artist = episode.Show?.Name,
                Album = episode.Show?.Name,
                ImageUrl = GetEpisodeImageUrl(episode),
            }
        };
    }

    // ── Metadata fetching ──

    private async Task<Track> FetchTrackMetadataAsync(string uri, SpotifyId trackId, CancellationToken ct)
    {
        if (_extendedMetadataClient != null)
        {
            var extTrack = await _extendedMetadataClient.GetTrackAudioFilesAsync(uri, ct);
            if (extTrack is { File.Count: > 0 })
                return extTrack;
        }

        var metadataBytes = await _spClient.GetTrackMetadataAsync(trackId.ToBase16(), ct);
        return Track.Parser.ParseFrom(metadataBytes);
    }

    // ── Audio file selection ──

    private async Task<(AudioFile? File, Track? UsedTrack)> SelectAudioFileAsync(
        Track track, AudioQuality quality, CancellationToken ct)
    {
        var file = SelectAudioFileFromTrack(track, quality);
        if (file != null)
            return (file, track);

        // Check alternatives
        foreach (var alt in track.Alternative)
        {
            if (alt.Gid == null || alt.Gid.Length == 0) continue;

            try
            {
                Track? altTrack = null;
                if (_extendedMetadataClient != null)
                {
                    var altUri = $"spotify:track:{SpotifyId.FromRaw(alt.Gid.Span, SpotifyIdType.Track).ToBase62()}";
                    altTrack = await _extendedMetadataClient.GetTrackAudioFilesAsync(altUri, ct);
                }

                if (altTrack == null || altTrack.File.Count == 0)
                {
                    var altGid = Convert.ToHexString(alt.Gid.ToByteArray()).ToLowerInvariant();
                    var bytes = await _spClient.GetTrackMetadataAsync(altGid, ct);
                    altTrack = Track.Parser.ParseFrom(bytes);
                }

                if (altTrack is { File.Count: > 0 })
                {
                    file = SelectAudioFileFromTrack(altTrack, quality);
                    if (file != null) return (file, altTrack);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to fetch alternative track");
            }
        }

        return (null, null);
    }

    private static AudioFile? SelectAudioFileFromTrack(Track track, AudioQuality quality)
    {
        if (track.File.Count == 0) return null;

        var preferredFormats = quality.GetPreferredFormats();

        foreach (var format in preferredFormats)
        {
            var protoFormat = MapToProtoFormat(format);
            var file = track.File.FirstOrDefault(f => f.Format == protoFormat);
            if (file != null) return file;
        }

        // Fallback: any Vorbis
        var anyVorbis = track.File.FirstOrDefault(f =>
            f.Format is AudioFile.Types.Format.OggVorbis96
                or AudioFile.Types.Format.OggVorbis160
                or AudioFile.Types.Format.OggVorbis320);
        if (anyVorbis != null) return anyVorbis;

        return track.File.FirstOrDefault();
    }

    private static AudioFile? SelectAudioFileFromEpisode(Episode episode, AudioQuality quality)
    {
        if (episode.Audio.Count == 0) return null;

        var preferredFormats = quality.GetPreferredFormats();
        foreach (var format in preferredFormats)
        {
            var protoFormat = MapToProtoFormat(format);
            var file = episode.Audio.FirstOrDefault(f => f.Format == protoFormat);
            if (file != null) return file;
        }

        return episode.Audio.FirstOrDefault();
    }

    // ── Normalization ──

    private static NormalizationData ReadNormalizationFromHeadData(byte[] headData)
    {
        if (headData.Length < NormalizationData.FileOffset + NormalizationData.Size)
            return NormalizationData.Default;
        return NormalizationData.Parse(headData.AsSpan(NormalizationData.FileOffset, NormalizationData.Size));
    }

    // ── Metadata building ──

    private static TrackMetadataDto BuildMetadataDto(string uri, Track track, NormalizationData normalization)
    {
        string? albumUri = null;
        if (track.Album?.Gid is { Length: > 0 })
            albumUri = $"spotify:album:{SpotifyId.FromRaw(track.Album.Gid.Span, SpotifyIdType.Album).ToBase62()}";

        string? artistUri = null;
        if (track.Artist.Count > 0 && track.Artist[0].Gid is { Length: > 0 })
            artistUri = $"spotify:artist:{SpotifyId.FromRaw(track.Artist[0].Gid.Span, SpotifyIdType.Artist).ToBase62()}";

        return new TrackMetadataDto
        {
            Title = track.Name,
            Artist = string.Join(", ", track.Artist.Select(a => a.Name)),
            Album = track.Album?.Name,
            AlbumUri = albumUri,
            ArtistUri = artistUri,
            ImageUrl = GetAlbumImageUrl(track.Album, Image.Types.Size.Default),
            ImageSmallUrl = GetAlbumImageUrl(track.Album, Image.Types.Size.Small),
            ImageLargeUrl = GetAlbumImageUrl(track.Album, Image.Types.Size.Large),
            ImageXLargeUrl = GetAlbumImageUrl(track.Album, Image.Types.Size.Xlarge),
        };
    }

    private static string? GetAlbumImageUrl(Album? album, Image.Types.Size preferredSize = Image.Types.Size.Default)
    {
        if (album?.CoverGroup?.Image.Count > 0)
        {
            var image = album.CoverGroup.Image.FirstOrDefault(i => i.Size == preferredSize)
                        ?? album.CoverGroup.Image.FirstOrDefault();
            if (image != null)
            {
                var imageId = Convert.ToHexString(image.FileId.ToByteArray()).ToLowerInvariant();
                return $"spotify:image:{imageId}";
            }
        }
        return null;
    }

    private static string? GetEpisodeImageUrl(Episode episode)
    {
        if (episode.CoverImage?.Image.Count > 0)
        {
            var image = episode.CoverImage.Image.FirstOrDefault();
            if (image?.FileId != null)
            {
                var imageId = Convert.ToHexString(image.FileId.ToByteArray()).ToLowerInvariant();
                return $"spotify:image:{imageId}";
            }
        }
        return null;
    }

    // ── Format mapping ──

    private static string GetCodecName(AudioFileFormat format) => format switch
    {
        AudioFileFormat.OGG_VORBIS_96 or AudioFileFormat.OGG_VORBIS_160 or AudioFileFormat.OGG_VORBIS_320 => "vorbis",
        AudioFileFormat.MP3_96 or AudioFileFormat.MP3_160 or AudioFileFormat.MP3_256 or AudioFileFormat.MP3_320 => "mp3",
        AudioFileFormat.FLAC_FLAC or AudioFileFormat.FLAC_FLAC_24BIT => "flac",
        AudioFileFormat.AAC_24 or AudioFileFormat.AAC_48 => "aac",
        AudioFileFormat.XHE_AAC_12 or AudioFileFormat.XHE_AAC_16 or AudioFileFormat.XHE_AAC_24 => "xhe-aac",
        _ => "unknown"
    };

    private static AudioFile.Types.Format MapToProtoFormat(AudioFileFormat format) => format switch
    {
        AudioFileFormat.OGG_VORBIS_96 => AudioFile.Types.Format.OggVorbis96,
        AudioFileFormat.OGG_VORBIS_160 => AudioFile.Types.Format.OggVorbis160,
        AudioFileFormat.OGG_VORBIS_320 => AudioFile.Types.Format.OggVorbis320,
        AudioFileFormat.MP3_256 => AudioFile.Types.Format.Mp3256,
        AudioFileFormat.MP3_320 => AudioFile.Types.Format.Mp3320,
        AudioFileFormat.MP3_160 => AudioFile.Types.Format.Mp3160,
        AudioFileFormat.MP3_96 => AudioFile.Types.Format.Mp396,
        AudioFileFormat.AAC_24 => AudioFile.Types.Format.Aac24,
        AudioFileFormat.AAC_48 => AudioFile.Types.Format.Aac48,
        AudioFileFormat.FLAC_FLAC => AudioFile.Types.Format.FlacFlac,
        AudioFileFormat.FLAC_FLAC_24BIT => AudioFile.Types.Format.FlacFlac24Bit,
        AudioFileFormat.XHE_AAC_24 => AudioFile.Types.Format.XheAac24,
        AudioFileFormat.XHE_AAC_16 => AudioFile.Types.Format.XheAac16,
        AudioFileFormat.XHE_AAC_12 => AudioFile.Types.Format.XheAac12,
        _ => AudioFile.Types.Format.OggVorbis160
    };

    private static AudioFileFormat MapToAudioFileFormat(AudioFile.Types.Format format) => format switch
    {
        AudioFile.Types.Format.OggVorbis96 => AudioFileFormat.OGG_VORBIS_96,
        AudioFile.Types.Format.OggVorbis160 => AudioFileFormat.OGG_VORBIS_160,
        AudioFile.Types.Format.OggVorbis320 => AudioFileFormat.OGG_VORBIS_320,
        AudioFile.Types.Format.Mp3256 => AudioFileFormat.MP3_256,
        AudioFile.Types.Format.Mp3320 => AudioFileFormat.MP3_320,
        AudioFile.Types.Format.Mp3160 => AudioFileFormat.MP3_160,
        AudioFile.Types.Format.Mp396 => AudioFileFormat.MP3_96,
        AudioFile.Types.Format.Aac24 => AudioFileFormat.AAC_24,
        AudioFile.Types.Format.Aac48 => AudioFileFormat.AAC_48,
        AudioFile.Types.Format.FlacFlac => AudioFileFormat.FLAC_FLAC,
        AudioFile.Types.Format.FlacFlac24Bit => AudioFileFormat.FLAC_FLAC_24BIT,
        AudioFile.Types.Format.XheAac24 => AudioFileFormat.XHE_AAC_24,
        AudioFile.Types.Format.XheAac16 => AudioFileFormat.XHE_AAC_16,
        AudioFile.Types.Format.XheAac12 => AudioFileFormat.XHE_AAC_12,
        _ => AudioFileFormat.OGG_VORBIS_160
    };
}
