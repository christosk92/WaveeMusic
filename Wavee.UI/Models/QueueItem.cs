using Wavee.Audio.Queue;

namespace Wavee.UI.Models;

/// <summary>
/// Represents a single item in the playback queue.
/// </summary>
public sealed record QueueItem
{
    public required string TrackId { get; init; }
    public required string Title { get; init; }
    public required string ArtistName { get; init; }
    public string? AlbumArt { get; init; }
    public double DurationMs { get; init; }

    /// <summary>
    /// True if the user manually added this to the queue (vs. auto-queued from context).
    /// </summary>
    public bool IsUserQueued { get; init; }

    /// <summary>
    /// Track source: "context", "queue", or "autoplay".
    /// </summary>
    public string Provider { get; init; } = "context";

    /// <summary>
    /// True if metadata is present (title + artist populated).
    /// </summary>
    public bool HasMetadata => !string.IsNullOrEmpty(Title) && !string.IsNullOrEmpty(ArtistName);

    /// <summary>
    /// Creates a QueueItem from a core QueueTrack.
    /// </summary>
    public static QueueItem FromQueueTrack(QueueTrack t) => new()
    {
        TrackId = t.Uri,
        Title = t.Title ?? string.Empty,
        ArtistName = t.Artist ?? string.Empty,
        AlbumArt = t.ImageUrl,
        DurationMs = t.DurationMs ?? 0,
        IsUserQueued = t.IsUserQueued,
        Provider = t.Provider,
    };
}
