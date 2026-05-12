using ATL;
using Microsoft.Extensions.Logging;

namespace Wavee.Local;

/// <summary>
/// Tag-and-duration extraction for local audio files. Backed by ATL.Net which
/// handles MP3/FLAC/WAV/M4A/OGG/Opus/WMA tag formats uniformly with no native
/// dependency. Pure-managed, AOT-friendly enough for our purposes.
/// </summary>
public sealed class LocalMetadataExtractor
{
    private readonly ILogger? _logger;

    public LocalMetadataExtractor(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Extracts metadata + first cover-art bytes from the given file path.
    /// Returns <c>null</c> if the file is not a recognised audio file or the
    /// extraction throws — caller logs and skips.
    /// </summary>
    public LocalFileMetadata? Extract(string filePath)
    {
        try
        {
            var track = new Track(filePath);
            if (track.Duration <= 0 && string.IsNullOrEmpty(track.Title))
            {
                _logger?.LogDebug("ATL returned empty metadata for {Path}", filePath);
                return null;
            }

            byte[]? coverBytes = null;
            string? coverMime = null;
            if (track.EmbeddedPictures is { Count: > 0 } pictures)
            {
                // Prefer cover front, fall back to first picture.
                var preferred = pictures.FirstOrDefault(p => p.PicType == PictureInfo.PIC_TYPE.Front)
                                ?? pictures[0];
                coverBytes = preferred.PictureData;
                coverMime = preferred.MimeType;
            }

            return new LocalFileMetadata
            {
                Title       = NullIfBlank(track.Title) ?? Path.GetFileNameWithoutExtension(filePath),
                Artist      = NullIfBlank(track.Artist),
                Album       = NullIfBlank(track.Album),
                AlbumArtist = NullIfBlank(track.AlbumArtist),
                Year        = track.Year > 0 ? track.Year : null,
                TrackNumber = track.TrackNumber > 0 ? track.TrackNumber : null,
                DiscNumber  = track.DiscNumber > 0 ? track.DiscNumber : null,
                Genre       = NullIfBlank(track.Genre),
                DurationMs  = track.DurationMs > 0 ? (long)track.DurationMs : track.Duration * 1000L,
                Bitrate     = (int)track.Bitrate,
                SampleRate  = (int)track.SampleRate,
                Channels    = track.ChannelsArrangement?.NbChannels ?? 0,
                CoverArtData = coverBytes,
                CoverArtMimeType = coverMime,
                FilePath    = filePath,
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Metadata extraction failed for {Path}", filePath);
            return null;
        }
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
