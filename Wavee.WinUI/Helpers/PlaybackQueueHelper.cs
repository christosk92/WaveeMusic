namespace Wavee.WinUI.Helpers;

/// <summary>
/// Defines how content should be inserted into the playback queue.
/// </summary>
public enum PlaybackInsertMode
{
    /// <summary>
    /// Replace the current queue and play immediately.
    /// </summary>
    PlayNow,

    /// <summary>
    /// Insert after the currently playing track (next in queue).
    /// </summary>
    PlayNext,

    /// <summary>
    /// Add to the end of the current queue (play when context ends).
    /// </summary>
    PlayLater
}

/// <summary>
/// Helper class for managing playback queue operations.
/// </summary>
public static class PlaybackQueueHelper
{
    /// <summary>
    /// Gets a human-readable description of the insert mode.
    /// </summary>
    public static string GetInsertModeDescription(PlaybackInsertMode mode)
    {
        return mode switch
        {
            PlaybackInsertMode.PlayNow => "Replace queue and play now",
            PlaybackInsertMode.PlayNext => "Play after current track",
            PlaybackInsertMode.PlayLater => "Add to end of queue",
            _ => "Unknown"
        };
    }

    // TODO: Implement queue management methods when PlaybackService is available
    // public static Task AddToQueue(string uri, PlaybackInsertMode mode)
    // public static Task ReplaceQueueAndPlay(string uri)
    // public static Task InsertNext(string uri)
    // public static Task InsertLater(string uri)
}
