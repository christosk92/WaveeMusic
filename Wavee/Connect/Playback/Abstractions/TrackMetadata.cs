namespace Wavee.Connect.Playback.Abstractions;

/// <summary>
/// Metadata for an audio track from any source.
/// </summary>
public sealed record TrackMetadata
{
    /// <summary>
    /// Track URI (spotify:track:xxx, file:///path, http://...).
    /// </summary>
    public required string Uri { get; init; }

    /// <summary>
    /// Track title/name.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Artist name(s), comma-separated if multiple.
    /// </summary>
    public string? Artist { get; init; }

    /// <summary>
    /// Album name.
    /// </summary>
    public string? Album { get; init; }

    /// <summary>
    /// Album artist (may differ from track artist for compilations).
    /// </summary>
    public string? AlbumArtist { get; init; }

    /// <summary>
    /// Track duration in milliseconds.
    /// </summary>
    public long? DurationMs { get; init; }

    /// <summary>
    /// Track number in album.
    /// </summary>
    public int? TrackNumber { get; init; }

    /// <summary>
    /// Disc number for multi-disc albums.
    /// </summary>
    public int? DiscNumber { get; init; }

    /// <summary>
    /// Release year.
    /// </summary>
    public int? Year { get; init; }

    /// <summary>
    /// Genre(s), comma-separated if multiple.
    /// </summary>
    public string? Genre { get; init; }

    /// <summary>
    /// Album art image URL or file path (medium/default size).
    /// </summary>
    public string? ImageUrl { get; init; }

    /// <summary>
    /// Small album art image URL (format: "spotify:image:{id}").
    /// </summary>
    public string? ImageSmallUrl { get; init; }

    /// <summary>
    /// Large album art image URL (format: "spotify:image:{id}").
    /// </summary>
    public string? ImageLargeUrl { get; init; }

    /// <summary>
    /// Extra-large album art image URL (format: "spotify:image:{id}").
    /// </summary>
    public string? ImageXLargeUrl { get; init; }

    /// <summary>
    /// Album URI (e.g., "spotify:album:xxx").
    /// </summary>
    public string? AlbumUri { get; init; }

    /// <summary>
    /// Primary artist URI (e.g., "spotify:artist:xxx").
    /// </summary>
    public string? ArtistUri { get; init; }

    /// <summary>
    /// ReplayGain track gain in dB (for normalization).
    /// </summary>
    public double? ReplayGainTrackGain { get; init; }

    /// <summary>
    /// ReplayGain album gain in dB (for normalization).
    /// </summary>
    public double? ReplayGainAlbumGain { get; init; }

    /// <summary>
    /// ReplayGain track peak value (0.0 - 1.0+).
    /// </summary>
    public double? ReplayGainTrackPeak { get; init; }

    /// <summary>
    /// Additional metadata key-value pairs.
    /// </summary>
    public IReadOnlyDictionary<string, string> AdditionalMetadata { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Creates an empty metadata object with only the URI.
    /// </summary>
    public static TrackMetadata CreateEmpty(string uri) => new() { Uri = uri };
}
