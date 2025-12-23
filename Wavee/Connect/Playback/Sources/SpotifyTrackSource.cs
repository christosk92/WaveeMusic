using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Wavee.Connect.Playback.Abstractions;
using Wavee.Connect.Playback.Decoders;
using Wavee.Core.Audio;
using Wavee.Core.Audio.Cache;
using Wavee.Core.Audio.Download;
using Wavee.Core.Crypto;
using Wavee.Core.Http;
using Wavee.Core.Session;
using Wavee.Protocol.ExtendedMetadata;
using Wavee.Protocol.Metadata;

namespace Wavee.Connect.Playback.Sources;

/// <summary>
/// Track source for Spotify audio files (tracks and episodes).
/// </summary>
/// <remarks>
/// Handles the complete flow for loading Spotify content:
/// 1. Parse URI -> SpotifyId (track or episode)
/// 2. Fetch metadata via extended-metadata API (includes audio files)
/// 3. Select audio file based on quality preference
/// 4. Fetch head file (for instant start) + AudioKey + CDN URL in parallel
/// 5. Create combined stream (head file + CDN progressive download)
/// 6. Wrap with decryption layer
///
/// Episodes are streamed identically to tracks - they have file IDs and use
/// the same CDN/storage mechanism. The Episode proto has an 'audio' field
/// (equivalent to Track's 'file' field) with available audio formats.
/// </remarks>
public sealed class SpotifyTrackSource : ITrackSource
{
    private readonly Session _session;
    private readonly SpClient _spClient;
    private readonly IExtendedMetadataClient? _extendedMetadataClient;
    private readonly HeadFileClient _headFileClient;
    private readonly HttpClient _httpClient;
    private readonly AudioCacheManager? _cache;
    private readonly AudioQuality _preferredQuality;
    private readonly ILogger? _logger;

    public string SourceName => "Spotify";

    /// <summary>
    /// Creates a new SpotifyTrackSource.
    /// </summary>
    /// <param name="session">Active Spotify session.</param>
    /// <param name="spClient">SpClient for metadata requests.</param>
    /// <param name="headFileClient">Client for fetching head files.</param>
    /// <param name="httpClient">HTTP client for CDN requests.</param>
    /// <param name="preferredQuality">Preferred audio quality.</param>
    /// <param name="cache">Optional audio cache manager.</param>
    /// <param name="extendedMetadataClient">Optional extended metadata client for audio files.</param>
    /// <param name="logger">Optional logger.</param>
    public SpotifyTrackSource(
        Session session,
        SpClient spClient,
        HeadFileClient headFileClient,
        HttpClient httpClient,
        AudioQuality preferredQuality = AudioQuality.VeryHigh,
        AudioCacheManager? cache = null,
        IExtendedMetadataClient? extendedMetadataClient = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(spClient);
        ArgumentNullException.ThrowIfNull(headFileClient);
        ArgumentNullException.ThrowIfNull(httpClient);

        _session = session;
        _spClient = spClient;
        _extendedMetadataClient = extendedMetadataClient;
        _headFileClient = headFileClient;
        _httpClient = httpClient;
        _preferredQuality = preferredQuality;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanHandle(string uri)
    {
        return uri.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("spotify:episode:", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<ITrackStream> LoadAsync(string uri, CancellationToken cancellationToken = default)
    {
        if (!CanHandle(uri))
            throw new ArgumentException($"Cannot handle URI: {uri}", nameof(uri));

        // Dispatch based on URI type
        if (uri.StartsWith("spotify:episode:", StringComparison.OrdinalIgnoreCase))
        {
            return await LoadEpisodeAsync(uri, cancellationToken);
        }

        return await LoadTrackAsync(uri, cancellationToken);
    }

    private async Task<ITrackStream> LoadTrackAsync(string uri, CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Loading track {Uri} with quality {Quality}", uri, _preferredQuality);

        // 1. Parse track URI
        var trackId = SpotifyId.FromUri(uri);

        // 2. Fetch track metadata - try extended-metadata first (includes audio files), fall back to basic API
        Track track;
        if (_extendedMetadataClient != null)
        {
            _logger?.LogDebug("Fetching track via extended-metadata API");
            var extTrack = await _extendedMetadataClient.GetTrackAudioFilesAsync(uri, cancellationToken);
            if (extTrack != null && extTrack.File.Count > 0)
            {
                track = extTrack;
                _logger?.LogDebug("Got {FileCount} audio files from extended-metadata API", track.File.Count);
            }
            else
            {
                // Extended metadata didn't return audio files, fall back to basic metadata
                _logger?.LogWarning("Extended-metadata returned no audio files, falling back to basic API");
                var metadataBytes = await _spClient.GetTrackMetadataAsync(trackId.ToBase16(), cancellationToken);
                track = Track.Parser.ParseFrom(metadataBytes);
            }
        }
        else
        {
            // No extended metadata client, use basic API
            var metadataBytes = await _spClient.GetTrackMetadataAsync(trackId.ToBase16(), cancellationToken);
            track = Track.Parser.ParseFrom(metadataBytes);
        }

        _logger?.LogDebug("Track: {Name} by {Artist}, duration={Duration}ms",
            track.Name,
            track.Artist.Count > 0 ? track.Artist[0].Name : "Unknown",
            track.Duration);

        // Log available audio files for debugging
        _logger?.LogDebug("Track has {FileCount} audio files:", track.File.Count);
        foreach (var file in track.File)
        {
            _logger?.LogDebug("  - Format: {Format}, FileId: {HasFileId}",
                file.Format, file.FileId.Length > 0 ? "present" : "missing");
        }

        // 3. Select audio file based on quality preference (also checks alternatives)
        var selectedFile = SelectAudioFile(track, _preferredQuality);
        if (selectedFile == null)
        {
            _logger?.LogWarning("No audio files found for track {Uri}, checked {AltCount} alternatives",
                uri, track.Alternative.Count);
            throw new InvalidOperationException(
                $"No suitable audio file found for track {uri}. " +
                $"Track has {track.File.Count} files, {track.Alternative.Count} alternatives.");
        }

        var fileId = FileId.FromBytes(selectedFile.FileId.Span);
        var audioFormat = MapToAudioFileFormat(selectedFile.Format);

        _logger?.LogDebug("Selected audio file: format={Format}, fileId={FileId}",
            audioFormat, fileId.ToBase16());

        // 4. Start all fetches in parallel (but don't wait for all)
        var headTask = _headFileClient.TryFetchHeadAsync(fileId, cancellationToken);
        var keyTask = _session.AudioKeys.RequestAudioKeyAsync(trackId, fileId, cancellationToken);
        var cdnTask = _spClient.ResolveAudioStorageAsync(fileId, cancellationToken);

        // 5. Wait ONLY for head file - this enables instant start!
        var headData = await headTask;

        // 6. If head file available and large enough for normalization, use lazy approach
        if (headData != null && headData.Length > NormalizationData.FileOffset + NormalizationData.Size)
        {
            _logger?.LogDebug("Using instant start with {HeadSize} bytes head data", headData.Length);

            // Read normalization directly from head bytes (already decrypted)
            var normalizationData = ReadNormalizationFromHeadData(headData);

            // Create lazy stream that defers CDN until head data exhausted
            var lazyStream = new LazyProgressiveDownloader(
                headData,
                keyTask,
                cdnTask,
                _httpClient,
                fileId,
                logger: _logger);

            // Build metadata
            var metadata = BuildMetadata(uri, track, normalizationData);

            _logger?.LogInformation("Returning track stream immediately (instant start enabled)");

            return new SpotifyTrackStream(lazyStream, metadata, normalizationData)
            {
                AudioFormat = audioFormat,
                FileId = fileId
            };
        }

        // 7. Fallback: No head file or too small - wait for all resources
        _logger?.LogDebug("No usable head file, waiting for all resources");

        await Task.WhenAll(keyTask, cdnTask);

        var audioKey = await keyTask;
        var cdnResponse = await cdnTask;

        if (cdnResponse.Cdnurl.Count == 0)
        {
            throw new InvalidOperationException($"No CDN URLs returned for file {fileId.ToBase16()}");
        }

        var cdnUrl = cdnResponse.Cdnurl[0];

        _logger?.LogDebug("CDN URL resolved, head file: {HeadSize} bytes",
            headData?.Length ?? 0);

        // 8. Get file size from CDN HEAD request or estimate
        var fileSize = await GetFileSizeAsync(cdnUrl, cancellationToken);

        // 9. Create progressive downloader with head data
        var downloader = new ProgressiveDownloader(
            _httpClient,
            cdnUrl,
            fileSize,
            fileId,
            headData,
            logger: _logger);

        // 9.1. Start background download of entire file
        downloader.StartBackgroundDownload();

        // 10. Read normalization data before wrapping with decrypt
        // Pass head length so we know not to double-decrypt head file data
        var fallbackNormalizationData = ReadNormalizationData(downloader, audioKey, headData?.Length ?? 0);

        // 11. Wrap with decryption layer
        // Head file data is already decrypted (0xa7 header), CDN data is encrypted
        // Pass head length as offset so decryption only applies to CDN portion
        var decryptStream = new AudioDecryptStream(audioKey, downloader, decryptionStartOffset: headData?.Length ?? 0);

        // 12. Build metadata
        var fallbackMetadata = BuildMetadata(uri, track, fallbackNormalizationData);

        return new SpotifyTrackStream(decryptStream, fallbackMetadata, fallbackNormalizationData, downloader)
        {
            AudioFormat = audioFormat,
            FileId = fileId
        };
    }

    private AudioFile? SelectAudioFile(Track track, AudioQuality quality)
    {
        // Try main track first
        var file = SelectAudioFileFromTrack(track, quality);
        if (file != null)
            return file;

        // If no files in main track, check alternatives
        foreach (var alt in track.Alternative)
        {
            file = SelectAudioFileFromTrack(alt, quality);
            if (file != null)
            {
                _logger?.LogDebug("Using alternative track for audio files");
                return file;
            }
        }

        return null;
    }

    private AudioFile? SelectAudioFileFromTrack(Track track, AudioQuality quality)
    {
        if (track.File.Count == 0)
            return null;

        var preferredFormats = quality.GetPreferredFormats();

        // First pass: look for exact match in preferred order
        foreach (var format in preferredFormats)
        {
            var protoFormat = MapToProtoFormat(format);
            var file = track.File.FirstOrDefault(f => f.Format == protoFormat);
            if (file != null)
                return file;
        }

        // Second pass: any Vorbis file
        var anyVorbis = track.File.FirstOrDefault(f =>
            f.Format == AudioFile.Types.Format.OggVorbis96 ||
            f.Format == AudioFile.Types.Format.OggVorbis160 ||
            f.Format == AudioFile.Types.Format.OggVorbis320);

        if (anyVorbis != null)
            return anyVorbis;

        // Third pass: any file
        return track.File.FirstOrDefault();
    }

    private static AudioFile.Types.Format MapToProtoFormat(AudioFileFormat format)
    {
        return format switch
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
    }

    private static AudioFileFormat MapToAudioFileFormat(AudioFile.Types.Format format)
    {
        return format switch
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

    private async Task<long> GetFileSizeAsync(string cdnUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, cdnUrl);
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.Content.Headers.ContentLength.HasValue)
            {
                return response.Content.Headers.ContentLength.Value;
            }
        }
        catch
        {
            // Fall through to estimate
        }

        // Estimate based on bitrate (3 minutes at 320kbps = ~7.2MB)
        return 8 * 1024 * 1024; // 8MB default estimate
    }

    private static NormalizationData ReadNormalizationData(Stream stream, byte[] audioKey, long headDataLength)
    {
        if (!stream.CanSeek || stream.Length < NormalizationData.FileOffset + NormalizationData.Size)
            return NormalizationData.Default;

        var originalPosition = stream.Position;
        try
        {
            stream.Position = NormalizationData.FileOffset;

            Span<byte> data = stackalloc byte[NormalizationData.Size];
            var bytesRead = stream.Read(data);

            if (bytesRead < NormalizationData.Size)
                return NormalizationData.Default;

            // Head file data is already decrypted (contains 0xa7 header)
            // Only decrypt if reading from CDN portion (beyond head data)
            if (NormalizationData.FileOffset >= headDataLength)
            {
                // Reading from CDN - need to decrypt
                using var decryptStream = new AudioDecryptStream(audioKey, new MemoryStream(data.ToArray()));
                Span<byte> decryptedData = stackalloc byte[NormalizationData.Size];
                decryptStream.Read(decryptedData);
                return NormalizationData.Parse(decryptedData);
            }

            // Reading from head file - already decrypted
            return NormalizationData.Parse(data);
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    /// <summary>
    /// Reads normalization data directly from head file bytes.
    /// Head data is already decrypted, so no decryption needed.
    /// </summary>
    private static NormalizationData ReadNormalizationFromHeadData(byte[] headData)
    {
        if (headData.Length < NormalizationData.FileOffset + NormalizationData.Size)
            return NormalizationData.Default;

        // Head file is already decrypted - just read the bytes at the offset
        var data = headData.AsSpan(NormalizationData.FileOffset, NormalizationData.Size);
        return NormalizationData.Parse(data);
    }

    private static TrackMetadata BuildMetadata(string uri, Track track, NormalizationData normalization)
    {
        return new TrackMetadata
        {
            Uri = uri,
            Title = track.Name,
            Artist = string.Join(", ", track.Artist.Select(a => a.Name)),
            Album = track.Album?.Name,
            AlbumArtist = track.Album?.Artist.Count > 0 ? track.Album.Artist[0].Name : null,
            DurationMs = track.Duration,
            TrackNumber = track.Number,
            DiscNumber = track.DiscNumber,
            Year = track.Album?.Date?.Year,
            ImageUrl = GetAlbumImageUrl(track.Album),
            ReplayGainTrackGain = normalization.TrackGainDb,
            ReplayGainAlbumGain = normalization.AlbumGainDb,
            ReplayGainTrackPeak = normalization.TrackPeak
        };
    }

    private static string? GetAlbumImageUrl(Album? album)
    {
        if (album?.CoverGroup?.Image.Count > 0)
        {
            // Prefer medium size image
            var image = album.CoverGroup.Image
                .OrderByDescending(i => i.Size == Image.Types.Size.Default ? 2 :
                                        i.Size == Image.Types.Size.Large ? 1 : 0)
                .FirstOrDefault();

            if (image != null)
            {
                // Convert file ID to Spotify CDN image URL
                var imageId = Convert.ToHexString(image.FileId.ToByteArray()).ToLowerInvariant();
                return $"https://i.scdn.co/image/{imageId}";
            }
        }

        return null;
    }

    #region Episode Loading

    /// <summary>
    /// Loads a Spotify episode for playback.
    /// </summary>
    /// <remarks>
    /// Episodes are streamed identically to tracks - they have file IDs and use
    /// the same CDN/storage mechanism. The Episode proto has an 'audio' field
    /// (equivalent to Track's 'file' field) with available audio formats.
    ///
    /// Some episodes may have an external URL (for non-Spotify hosted content),
    /// which we handle by delegating to HTTP streaming.
    /// </remarks>
    private async Task<ITrackStream> LoadEpisodeAsync(string uri, CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Loading episode {Uri} with quality {Quality}", uri, _preferredQuality);

        // 1. Parse episode URI
        var episodeId = SpotifyId.FromUri(uri);

        // 2. Fetch episode metadata
        var metadataBytes = await _spClient.GetEpisodeMetadataAsync(episodeId.ToBase62(), cancellationToken);
        var episode = Episode.Parser.ParseFrom(metadataBytes);

        _logger?.LogDebug("Episode: {Name}, duration={Duration}ms, has external URL={HasExternalUrl}",
            episode.Name, episode.Duration, episode.HasExternalUrl);

        // 3. Check for external URL (non-Spotify hosted content)
        if (episode.HasExternalUrl && !string.IsNullOrEmpty(episode.ExternalUrl))
        {
            _logger?.LogDebug("Episode has external URL, delegating to HTTP streaming: {Url}", episode.ExternalUrl);
            return await LoadExternalEpisodeAsync(uri, episode, cancellationToken);
        }

        // 4. Log available audio files
        _logger?.LogDebug("Episode has {AudioCount} audio files:", episode.Audio.Count);
        foreach (var audio in episode.Audio)
        {
            _logger?.LogDebug("  - Format: {Format}, FileId: {HasFileId}",
                audio.Format, audio.FileId.Length > 0 ? "present" : "missing");
        }

        // 5. Select audio file based on quality preference
        var selectedFile = SelectEpisodeAudioFile(episode, _preferredQuality);
        if (selectedFile == null)
        {
            throw new InvalidOperationException($"No suitable audio file found for episode {uri}. Episode has {episode.Audio.Count} audio files.");
        }

        var fileId = FileId.FromBytes(selectedFile.FileId.Span);
        var audioFormat = MapToAudioFileFormat(selectedFile.Format);

        _logger?.LogDebug("Selected audio file: format={Format}, fileId={FileId}",
            audioFormat, fileId.ToBase16());

        // 6. Start all fetches in parallel
        var headTask = _headFileClient.TryFetchHeadAsync(fileId, cancellationToken);
        var keyTask = _session.AudioKeys.RequestAudioKeyAsync(episodeId, fileId, cancellationToken);
        var cdnTask = _spClient.ResolveAudioStorageAsync(fileId, cancellationToken);

        // 7. Wait for head file first (for instant start)
        var headData = await headTask;

        // 8. If head file available and large enough, use lazy approach
        if (headData != null && headData.Length > NormalizationData.FileOffset + NormalizationData.Size)
        {
            _logger?.LogDebug("Using instant start with {HeadSize} bytes head data", headData.Length);

            var normalizationData = ReadNormalizationFromHeadData(headData);

            var lazyStream = new LazyProgressiveDownloader(
                headData,
                keyTask,
                cdnTask,
                _httpClient,
                fileId,
                logger: _logger);

            var metadata = BuildEpisodeMetadata(uri, episode, normalizationData);

            _logger?.LogInformation("Returning episode stream immediately (instant start enabled)");

            return new SpotifyTrackStream(lazyStream, metadata, normalizationData)
            {
                AudioFormat = audioFormat,
                FileId = fileId
            };
        }

        // 9. Fallback: No head file - wait for all resources
        _logger?.LogDebug("No usable head file, waiting for all resources");

        await Task.WhenAll(keyTask, cdnTask);

        var audioKey = await keyTask;
        var cdnResponse = await cdnTask;

        if (cdnResponse.Cdnurl.Count == 0)
        {
            throw new InvalidOperationException($"No CDN URLs returned for file {fileId.ToBase16()}");
        }

        var cdnUrl = cdnResponse.Cdnurl[0];

        var fileSize = await GetFileSizeAsync(cdnUrl, cancellationToken);

        var downloader = new ProgressiveDownloader(
            _httpClient,
            cdnUrl,
            fileSize,
            fileId,
            headData,
            logger: _logger);

        downloader.StartBackgroundDownload();

        var fallbackNormalizationData = ReadNormalizationData(downloader, audioKey, headData?.Length ?? 0);
        var decryptStream = new AudioDecryptStream(audioKey, downloader, decryptionStartOffset: headData?.Length ?? 0);
        var fallbackMetadata = BuildEpisodeMetadata(uri, episode, fallbackNormalizationData);

        return new SpotifyTrackStream(decryptStream, fallbackMetadata, fallbackNormalizationData, downloader)
        {
            AudioFormat = audioFormat,
            FileId = fileId
        };
    }

    /// <summary>
    /// Loads an episode with external URL via HTTP streaming.
    /// </summary>
    private async Task<ITrackStream> LoadExternalEpisodeAsync(string uri, Episode episode, CancellationToken cancellationToken)
    {
        // Create HTTP request for external URL
        var request = new HttpRequestMessage(HttpMethod.Get, episode.ExternalUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", "Wavee/1.0 (Podcast Player)");

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new HttpRequestException($"Failed to load external episode: {response.StatusCode}");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bufferedStream = new BufferedHttpStream(stream, 0);
        await bufferedStream.PreBufferAsync(131072, cancellationToken);
        var urlAwareStream = new UrlAwareStream(bufferedStream, episode.ExternalUrl);

        var metadata = BuildEpisodeMetadata(uri, episode, NormalizationData.Default);

        return new SpotifyEpisodeExternalStream(urlAwareStream, metadata, response);
    }

    private AudioFile? SelectEpisodeAudioFile(Episode episode, AudioQuality quality)
    {
        if (episode.Audio.Count == 0)
            return null;

        var preferredFormats = quality.GetPreferredFormats();

        // First pass: look for exact match in preferred order
        foreach (var format in preferredFormats)
        {
            var protoFormat = MapToProtoFormat(format);
            var file = episode.Audio.FirstOrDefault(f => f.Format == protoFormat);
            if (file != null)
                return file;
        }

        // Second pass: any Vorbis file
        var anyVorbis = episode.Audio.FirstOrDefault(f =>
            f.Format == AudioFile.Types.Format.OggVorbis96 ||
            f.Format == AudioFile.Types.Format.OggVorbis160 ||
            f.Format == AudioFile.Types.Format.OggVorbis320);

        if (anyVorbis != null)
            return anyVorbis;

        // Third pass: any file
        return episode.Audio.FirstOrDefault();
    }

    private static TrackMetadata BuildEpisodeMetadata(string uri, Episode episode, NormalizationData normalization)
    {
        return new TrackMetadata
        {
            Uri = uri,
            Title = episode.Name,
            Artist = episode.Show?.Publisher ?? episode.Show?.Name,
            Album = episode.Show?.Name,
            DurationMs = episode.Duration,
            ImageUrl = GetEpisodeImageUrl(episode),
            ReplayGainTrackGain = normalization.TrackGainDb,
            ReplayGainAlbumGain = normalization.AlbumGainDb,
            ReplayGainTrackPeak = normalization.TrackPeak,
            AdditionalMetadata = new Dictionary<string, string>
            {
                ["source"] = "spotify",
                ["type"] = "episode",
                ["episodeNumber"] = episode.Number.ToString(),
                ["publishTime"] = episode.PublishTime?.ToString() ?? ""
            }
        };
    }

    private static string? GetEpisodeImageUrl(Episode episode)
    {
        // Try episode cover first
        if (episode.CoverImage?.Image.Count > 0)
        {
            var image = episode.CoverImage.Image
                .OrderByDescending(i => i.Size == Image.Types.Size.Default ? 2 :
                                        i.Size == Image.Types.Size.Large ? 1 : 0)
                .FirstOrDefault();

            if (image != null)
            {
                var imageId = Convert.ToHexString(image.FileId.ToByteArray()).ToLowerInvariant();
                return $"https://i.scdn.co/image/{imageId}";
            }
        }

        // Fall back to show cover
        if (episode.Show?.CoverImage?.Image.Count > 0)
        {
            var image = episode.Show.CoverImage.Image
                .OrderByDescending(i => i.Size == Image.Types.Size.Default ? 2 :
                                        i.Size == Image.Types.Size.Large ? 1 : 0)
                .FirstOrDefault();

            if (image != null)
            {
                var imageId = Convert.ToHexString(image.FileId.ToByteArray()).ToLowerInvariant();
                return $"https://i.scdn.co/image/{imageId}";
            }
        }

        return null;
    }

    #endregion
}
