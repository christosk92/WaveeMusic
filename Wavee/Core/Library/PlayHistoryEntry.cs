namespace Wavee.Core.Library;

/// <summary>
/// Represents a single play event in the history.
/// </summary>
/// <remarks>
/// Each time a track is played, a new entry is created.
/// This enables statistics like play count, listening time, and recently played.
/// </remarks>
public sealed record PlayHistoryEntry
{
    /// <summary>
    /// Auto-generated unique ID for this entry.
    /// </summary>
    public long Id { get; init; }

    /// <summary>
    /// The library item that was played (foreign key to library_items.id).
    /// </summary>
    public required string ItemId { get; init; }

    /// <summary>
    /// When playback started (Unix timestamp).
    /// </summary>
    public required long PlayedAt { get; init; }

    /// <summary>
    /// How long the track was actually played (in milliseconds).
    /// May be less than the track duration if skipped.
    /// </summary>
    public long DurationPlayedMs { get; init; }

    /// <summary>
    /// Whether the track was played to completion (not skipped).
    /// </summary>
    public bool Completed { get; init; }

    /// <summary>
    /// The context in which the track was played (e.g., playlist URI, album URI).
    /// Null if played directly.
    /// </summary>
    public string? SourceContext { get; init; }

    /// <summary>
    /// Creates a new play history entry with current timestamp.
    /// </summary>
    public static PlayHistoryEntry Create(
        string itemId,
        long durationPlayedMs,
        bool completed,
        string? sourceContext = null)
    {
        return new PlayHistoryEntry
        {
            ItemId = itemId,
            PlayedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            DurationPlayedMs = durationPlayedMs,
            Completed = completed,
            SourceContext = sourceContext
        };
    }

    /// <summary>
    /// Gets a display string for when this was played.
    /// </summary>
    public string GetPlayedAtDisplay()
    {
        var playedAt = DateTimeOffset.FromUnixTimeSeconds(PlayedAt);
        var now = DateTimeOffset.UtcNow;
        var diff = now - playedAt;

        if (diff.TotalMinutes < 1) return "Just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";

        return playedAt.LocalDateTime.ToString("MMM d, yyyy");
    }

    /// <summary>
    /// Gets a display string for duration played.
    /// </summary>
    public string GetDurationPlayedDisplay()
    {
        if (DurationPlayedMs <= 0) return "--:--";

        var span = TimeSpan.FromMilliseconds(DurationPlayedMs);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{span.Minutes}:{span.Seconds:D2}";
    }
}

/// <summary>
/// Statistics about library usage.
/// </summary>
public sealed record LibraryStats
{
    /// <summary>
    /// Total number of items in the library.
    /// </summary>
    public int TotalItems { get; init; }

    /// <summary>
    /// Number of Spotify tracks.
    /// </summary>
    public int SpotifyTracks { get; init; }

    /// <summary>
    /// Number of local files.
    /// </summary>
    public int LocalFiles { get; init; }

    /// <summary>
    /// Number of saved streams.
    /// </summary>
    public int Streams { get; init; }

    /// <summary>
    /// Number of podcast episodes.
    /// </summary>
    public int PodcastEpisodes { get; init; }

    /// <summary>
    /// Total number of plays recorded.
    /// </summary>
    public int TotalPlays { get; init; }

    /// <summary>
    /// Total listening time in milliseconds.
    /// </summary>
    public long TotalListeningTimeMs { get; init; }

    /// <summary>
    /// Gets listening time as a formatted string.
    /// </summary>
    public string GetListeningTimeDisplay()
    {
        var span = TimeSpan.FromMilliseconds(TotalListeningTimeMs);
        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m";
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{span.Minutes}m";
    }
}

/// <summary>
/// Query parameters for searching the library.
/// </summary>
public sealed record LibrarySearchQuery
{
    /// <summary>
    /// Text to search for in title, artist, album.
    /// </summary>
    public string? SearchText { get; init; }

    /// <summary>
    /// Filter by source type (null = all sources).
    /// </summary>
    public SourceType? SourceType { get; init; }

    /// <summary>
    /// Filter by artist name (substring match).
    /// </summary>
    public string? Artist { get; init; }

    /// <summary>
    /// Filter by album name (substring match).
    /// </summary>
    public string? Album { get; init; }

    /// <summary>
    /// Filter by genre (substring match).
    /// </summary>
    public string? Genre { get; init; }

    /// <summary>
    /// Minimum duration in milliseconds.
    /// </summary>
    public long? MinDurationMs { get; init; }

    /// <summary>
    /// Maximum duration in milliseconds.
    /// </summary>
    public long? MaxDurationMs { get; init; }

    /// <summary>
    /// Minimum release year.
    /// </summary>
    public int? MinYear { get; init; }

    /// <summary>
    /// Maximum release year.
    /// </summary>
    public int? MaxYear { get; init; }

    /// <summary>
    /// Sort order for results.
    /// </summary>
    public LibrarySortOrder SortOrder { get; init; } = LibrarySortOrder.RecentlyAdded;

    /// <summary>
    /// Maximum number of results to return.
    /// </summary>
    public int Limit { get; init; } = 50;

    /// <summary>
    /// Number of results to skip (for pagination).
    /// </summary>
    public int Offset { get; init; }
}

/// <summary>
/// Sort order options for library queries.
/// </summary>
public enum LibrarySortOrder
{
    /// <summary>
    /// Most recently added first.
    /// </summary>
    RecentlyAdded,

    /// <summary>
    /// Most recently played first.
    /// </summary>
    RecentlyPlayed,

    /// <summary>
    /// Most played first.
    /// </summary>
    MostPlayed,

    /// <summary>
    /// Alphabetically by title.
    /// </summary>
    Title,

    /// <summary>
    /// Alphabetically by artist.
    /// </summary>
    Artist,

    /// <summary>
    /// Alphabetically by album.
    /// </summary>
    Album,

    /// <summary>
    /// By duration (shortest first).
    /// </summary>
    Duration,

    /// <summary>
    /// By release year (newest first).
    /// </summary>
    Year
}
