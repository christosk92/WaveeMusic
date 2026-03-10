namespace Wavee.UI.WinUI.Data.Models;

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
}
