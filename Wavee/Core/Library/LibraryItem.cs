namespace Wavee.Core.Library;

/// <summary>
/// Represents a unified library item from any source (Spotify, local file, stream, podcast).
/// </summary>
/// <remarks>
/// This is the core model that unifies all playable content in the library.
/// Each item has a unique ID (typically a URI) and tracks its source type.
/// </remarks>
public sealed record LibraryItem
{
    /// <summary>
    /// Unique identifier for the item.
    /// Format depends on source:
    /// - Spotify: spotify:track:xxx, spotify:album:xxx
    /// - Local: file:///path/to/file.mp3
    /// - Stream: http://stream.url/audio
    /// - Podcast: podcast:episode:xxx
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The source type of this item.
    /// </summary>
    public required SourceType SourceType { get; init; }

    /// <summary>
    /// Display title of the item.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Artist or creator name (null for streams without metadata).
    /// </summary>
    public string? Artist { get; init; }

    /// <summary>
    /// Album name (null for singles, streams, podcasts).
    /// </summary>
    public string? Album { get; init; }

    /// <summary>
    /// Duration in milliseconds (0 for live streams).
    /// </summary>
    public long DurationMs { get; init; }

    /// <summary>
    /// Release year (null if unknown).
    /// </summary>
    public int? Year { get; init; }

    /// <summary>
    /// Genre (null if unknown).
    /// </summary>
    public string? Genre { get; init; }

    /// <summary>
    /// URL to cover art/thumbnail image.
    /// </summary>
    public string? ImageUrl { get; init; }

    /// <summary>
    /// Local file path (for LocalFile source type only).
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// Stream URL (for HttpStream source type only).
    /// </summary>
    public string? StreamUrl { get; init; }

    /// <summary>
    /// When the item was first added to the library (Unix timestamp).
    /// </summary>
    public long AddedAt { get; init; }

    /// <summary>
    /// When the item was last updated (Unix timestamp).
    /// </summary>
    public long UpdatedAt { get; init; }

    /// <summary>
    /// Additional metadata as JSON (format-specific tags, etc.).
    /// </summary>
    public string? MetadataJson { get; init; }

    /// <summary>
    /// Total play count (derived from play_history).
    /// </summary>
    public int PlayCount { get; init; }

    /// <summary>
    /// Last played timestamp (derived from play_history).
    /// </summary>
    public long? LastPlayedAt { get; init; }

    /// <summary>
    /// Creates a new LibraryItem with current timestamp for AddedAt and UpdatedAt.
    /// </summary>
    public static LibraryItem Create(
        string id,
        SourceType sourceType,
        string title,
        string? artist = null,
        string? album = null,
        long durationMs = 0,
        int? year = null,
        string? genre = null,
        string? imageUrl = null,
        string? filePath = null,
        string? streamUrl = null,
        string? metadataJson = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new LibraryItem
        {
            Id = id,
            SourceType = sourceType,
            Title = title,
            Artist = artist,
            Album = album,
            DurationMs = durationMs,
            Year = year,
            Genre = genre,
            ImageUrl = imageUrl,
            FilePath = filePath,
            StreamUrl = streamUrl,
            AddedAt = now,
            UpdatedAt = now,
            MetadataJson = metadataJson
        };
    }

    /// <summary>
    /// Creates a LibraryItem from a Spotify track.
    /// </summary>
    public static LibraryItem FromSpotifyTrack(
        string uri,
        string title,
        string? artist,
        string? album,
        long durationMs,
        int? year = null,
        string? imageUrl = null)
    {
        return Create(
            id: uri,
            sourceType: SourceType.Spotify,
            title: title,
            artist: artist,
            album: album,
            durationMs: durationMs,
            year: year,
            imageUrl: imageUrl);
    }

    /// <summary>
    /// Creates a LibraryItem from a local file.
    /// </summary>
    public static LibraryItem FromLocalFile(
        string filePath,
        string title,
        string? artist,
        string? album,
        long durationMs,
        int? year = null,
        string? genre = null,
        string? imageUrl = null)
    {
        var uri = $"file:///{filePath.Replace('\\', '/').TrimStart('/')}";
        return Create(
            id: uri,
            sourceType: SourceType.LocalFile,
            title: title,
            artist: artist,
            album: album,
            durationMs: durationMs,
            year: year,
            genre: genre,
            imageUrl: imageUrl,
            filePath: filePath);
    }

    /// <summary>
    /// Creates a LibraryItem from an HTTP stream.
    /// </summary>
    public static LibraryItem FromHttpStream(
        string url,
        string title,
        string? artist = null,
        string? imageUrl = null)
    {
        return Create(
            id: url,
            sourceType: SourceType.HttpStream,
            title: title,
            artist: artist,
            durationMs: 0, // Live streams have no duration
            imageUrl: imageUrl,
            streamUrl: url);
    }

    /// <summary>
    /// Gets a display string for duration (MM:SS or HH:MM:SS).
    /// </summary>
    public string GetDurationDisplay()
    {
        if (DurationMs <= 0) return "--:--";

        var span = TimeSpan.FromMilliseconds(DurationMs);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{span.Minutes}:{span.Seconds:D2}";
    }

    /// <summary>
    /// Gets a display string combining artist and album.
    /// </summary>
    public string GetArtistAlbumDisplay()
    {
        if (!string.IsNullOrEmpty(Artist) && !string.IsNullOrEmpty(Album))
            return $"{Artist} - {Album}";
        return Artist ?? Album ?? SourceType.GetDisplayName();
    }
}
