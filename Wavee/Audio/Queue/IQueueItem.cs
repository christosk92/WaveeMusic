namespace Wavee.Audio.Queue;

/// <summary>
/// Base interface for all items in the playback queue.
/// Tracks, page markers, and delimiters all implement this.
/// </summary>
public interface IQueueItem
{
    string Uri { get; }
    string? Uid { get; }
    string Provider { get; }
    bool IsTrack { get; }
}

/// <summary>
/// Page boundary marker from cluster state.
/// Signals a pagination boundary for lazy loading of next page.
/// URI format: spotify:meta:page:{PageNumber}
/// </summary>
public record QueuePageMarker(int PageNumber, string Provider = "autoplay") : IQueueItem
{
    public string Uri => $"spotify:meta:page:{PageNumber}";
    public string? Uid => $"page{PageNumber}_0";
    public bool IsTrack => false;
}

/// <summary>
/// End-of-content delimiter from cluster state.
/// Defines behavior when the queue runs out (pause or resume on advance/skip).
/// </summary>
public record QueueDelimiter(
    string AdvanceAction,
    string SkipAction,
    string Provider = "autoplay"
) : IQueueItem
{
    public string Uri => "spotify:delimiter";
    public string? Uid => "delimiter0";
    public bool IsTrack => false;
}
