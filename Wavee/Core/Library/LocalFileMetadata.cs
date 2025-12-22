namespace Wavee.Core.Library;

/// <summary>
/// Metadata extracted from a local audio file.
/// </summary>
public sealed record LocalFileMetadata
{
    /// <summary>
    /// Track title (from ID3/Vorbis tags).
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Artist name.
    /// </summary>
    public string? Artist { get; init; }

    /// <summary>
    /// Album name.
    /// </summary>
    public string? Album { get; init; }

    /// <summary>
    /// Album artist (may differ from track artist on compilations).
    /// </summary>
    public string? AlbumArtist { get; init; }

    /// <summary>
    /// Release year.
    /// </summary>
    public int? Year { get; init; }

    /// <summary>
    /// Track number on the album.
    /// </summary>
    public int? TrackNumber { get; init; }

    /// <summary>
    /// Disc number for multi-disc albums.
    /// </summary>
    public int? DiscNumber { get; init; }

    /// <summary>
    /// Genre.
    /// </summary>
    public string? Genre { get; init; }

    /// <summary>
    /// Duration in milliseconds.
    /// </summary>
    public long DurationMs { get; init; }

    /// <summary>
    /// Bitrate in kbps.
    /// </summary>
    public int Bitrate { get; init; }

    /// <summary>
    /// Sample rate in Hz.
    /// </summary>
    public int SampleRate { get; init; }

    /// <summary>
    /// Number of audio channels.
    /// </summary>
    public int Channels { get; init; }

    /// <summary>
    /// Embedded album art data (raw bytes).
    /// </summary>
    public byte[]? CoverArtData { get; init; }

    /// <summary>
    /// MIME type of the cover art (e.g., "image/jpeg", "image/png").
    /// </summary>
    public string? CoverArtMimeType { get; init; }

    /// <summary>
    /// The file path this metadata was extracted from.
    /// </summary>
    public string? FilePath { get; init; }
}
