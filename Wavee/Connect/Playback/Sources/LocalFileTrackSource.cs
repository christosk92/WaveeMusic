using Wavee.Connect.Playback.Abstractions;
using Wavee.Core.Library;

namespace Wavee.Connect.Playback.Sources;

/// <summary>
/// Track source for loading local audio files.
/// Handles file:// URIs and raw file paths.
/// </summary>
public sealed class LocalFileTrackSource : ITrackSource
{
    private readonly IMetadataExtractor _metadataExtractor;
    private readonly IAlbumArtCache? _albumArtCache;

    /// <summary>
    /// Creates a new local file track source.
    /// </summary>
    /// <param name="metadataExtractor">Metadata extractor for reading tags.</param>
    /// <param name="albumArtCache">Optional album art cache for embedded images.</param>
    public LocalFileTrackSource(IMetadataExtractor metadataExtractor, IAlbumArtCache? albumArtCache = null)
    {
        _metadataExtractor = metadataExtractor ?? throw new ArgumentNullException(nameof(metadataExtractor));
        _albumArtCache = albumArtCache;
    }

    /// <inheritdoc/>
    public string SourceName => "LocalFile";

    /// <inheritdoc/>
    public bool CanHandle(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return false;

        // Handle file:// URIs
        if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var path = new Uri(uri).LocalPath;
                return _metadataExtractor.IsSupportedExtension(path);
            }
            catch
            {
                return false;
            }
        }

        // Handle raw file paths (Windows or Unix-style)
        if (Path.IsPathRooted(uri) || uri.StartsWith("./") || uri.StartsWith("../"))
        {
            return File.Exists(uri) && _metadataExtractor.IsSupportedExtension(uri);
        }

        return false;
    }

    /// <inheritdoc/>
    public async Task<ITrackStream> LoadAsync(string uri, CancellationToken cancellationToken = default)
    {
        var filePath = NormalizePath(uri);

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Audio file not found: {filePath}", filePath);

        // Extract metadata from the file
        var localMetadata = await _metadataExtractor.ExtractAsync(filePath);

        // Cache album art if available
        string? imageUrl = null;
        if (_albumArtCache != null && localMetadata.CoverArtData != null)
        {
            var artPath = await _albumArtCache.CacheArtAsync(
                localMetadata.CoverArtData,
                localMetadata.CoverArtMimeType);

            if (artPath != null)
            {
                // Use file:// URI for the cached image
                imageUrl = new Uri(artPath).AbsoluteUri;
            }
        }

        // Build track metadata
        var trackMetadata = new TrackMetadata
        {
            Uri = uri.StartsWith("file://") ? uri : new Uri(filePath).AbsoluteUri,
            Title = localMetadata.Title ?? Path.GetFileNameWithoutExtension(filePath),
            Artist = localMetadata.Artist,
            Album = localMetadata.Album,
            AlbumArtist = localMetadata.AlbumArtist,
            DurationMs = localMetadata.DurationMs,
            TrackNumber = localMetadata.TrackNumber,
            DiscNumber = localMetadata.DiscNumber,
            Year = localMetadata.Year,
            Genre = localMetadata.Genre,
            ImageUrl = imageUrl,
            AdditionalMetadata = new Dictionary<string, string>
            {
                ["source"] = "local",
                ["filePath"] = filePath,
                ["bitrate"] = localMetadata.Bitrate.ToString(),
                ["sampleRate"] = localMetadata.SampleRate.ToString(),
                ["channels"] = localMetadata.Channels.ToString()
            }
        };

        return new LocalFileTrackStream(filePath, trackMetadata);
    }

    private static string NormalizePath(string uri)
    {
        if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(uri).LocalPath;
        }

        // Already a file path
        return Path.GetFullPath(uri);
    }
}
