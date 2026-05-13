namespace Wavee.UI.Models;

/// <summary>
/// Options for starting playback within a context (album, playlist, artist, etc.).
/// </summary>
public sealed record PlayContextOptions
{
    /// <summary>
    /// Zero-based index of the track to start playing within the context.
    /// </summary>
    public int? StartIndex { get; init; }

    /// <summary>
    /// URI of a specific track to start playing (overrides StartIndex).
    /// </summary>
    public string? StartTrackUri { get; init; }

    /// <summary>
    /// Position within the track to start from, in milliseconds.
    /// </summary>
    public long? PositionMs { get; init; }

    /// <summary>
    /// Whether to enable shuffle for the context.
    /// </summary>
    public bool? Shuffle { get; init; }

    /// <summary>
    /// Feature identifier for play origin tracking (e.g. "artist_page", "album_page").
    /// </summary>
    public string? PlayOriginFeature { get; init; }

    /// <summary>
    /// Executes the play command directly without applying the user's generic
    /// play-next/play-later prompt. Use for explicit commands such as radio.
    /// </summary>
    public bool BypassPrompt { get; init; }
}
