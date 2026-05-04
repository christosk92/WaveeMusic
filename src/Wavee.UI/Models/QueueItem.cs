using System.Collections.Generic;
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
    /// Stable per-track uid within the source context (lower-case hex), derived
    /// from the playlist/album/artist API's uid field. Published as
    /// <c>ProvidedTrack.uid</c> so remote clients can address this specific
    /// instance for skip-to-uid.
    /// </summary>
    public string? Uid { get; init; }

    /// <summary>
    /// Per-track metadata passed through to <c>ProvidedTrack.metadata</c> —
    /// recommender decorations (<c>item-score</c>, <c>decision_id</c>,
    /// <c>core:list_uid</c>, <c>PROBABLY_IN_*</c>, etc.) returned by the
    /// playlist API.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

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
