using ATL;

namespace Wavee.Core.Library;

/// <summary>
/// Interface for extracting metadata from local audio files.
/// </summary>
public interface IMetadataExtractor
{
    /// <summary>
    /// Extracts metadata from an audio file.
    /// </summary>
    /// <param name="filePath">Path to the audio file.</param>
    /// <returns>Extracted metadata including tags and embedded art.</returns>
    Task<LocalFileMetadata> ExtractAsync(string filePath);

    /// <summary>
    /// Checks if the file extension is a supported audio format.
    /// </summary>
    bool IsSupportedExtension(string filePath);
}

/// <summary>
/// Metadata extractor using ATL (Audio Tools Library).
/// </summary>
public sealed class MetadataExtractor : IMetadataExtractor
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".ogg", ".wav", ".m4a", ".aac", ".wma", ".opus", ".ape", ".wv"
    };

    /// <inheritdoc/>
    public bool IsSupportedExtension(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return SupportedExtensions.Contains(ext);
    }

    /// <inheritdoc/>
    public Task<LocalFileMetadata> ExtractAsync(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Audio file not found: {filePath}");

        // ATL Track class reads metadata synchronously
        var track = new Track(filePath);

        byte[]? coverData = null;
        string? coverMime = null;

        // Extract embedded picture if present
        if (track.EmbeddedPictures.Count > 0)
        {
            // Prefer front cover, otherwise use first picture
            var pic = track.EmbeddedPictures.FirstOrDefault(p =>
                p.PicType == ATL.PictureInfo.PIC_TYPE.Front) ?? track.EmbeddedPictures[0];

            coverData = pic.PictureData;
            coverMime = pic.MimeType;

            // Fallback MIME type detection from data if not provided
            if (string.IsNullOrEmpty(coverMime) && coverData != null && coverData.Length > 4)
            {
                coverMime = DetectImageMimeType(coverData);
            }
        }

        var metadata = new LocalFileMetadata
        {
            Title = NullIfEmpty(track.Title),
            Artist = NullIfEmpty(track.Artist),
            Album = NullIfEmpty(track.Album),
            AlbumArtist = NullIfEmpty(track.AlbumArtist),
            Year = track.Year > 0 ? track.Year : null,
            TrackNumber = track.TrackNumber > 0 ? track.TrackNumber : null,
            DiscNumber = track.DiscNumber > 0 ? track.DiscNumber : null,
            Genre = NullIfEmpty(track.Genre),
            DurationMs = (long)(track.Duration * 1000),
            Bitrate = track.Bitrate,
            SampleRate = (int)track.SampleRate,
            Channels = track.ChannelsArrangement?.NbChannels ?? 2,
            CoverArtData = coverData,
            CoverArtMimeType = coverMime,
            FilePath = filePath
        };

        return Task.FromResult(metadata);
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string DetectImageMimeType(byte[] data)
    {
        // Check magic bytes
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return "image/jpeg";

        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            return "image/png";

        if (data.Length >= 6 && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46)
            return "image/gif";

        if (data.Length >= 4 && data[0] == 0x42 && data[1] == 0x4D)
            return "image/bmp";

        // Default to JPEG
        return "image/jpeg";
    }
}
