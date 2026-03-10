namespace Wavee.UI.WinUI.Data.Models;

/// <summary>
/// Describes what is currently being played (the playback context).
/// </summary>
public sealed record PlaybackContextInfo
{
    /// <summary>
    /// URI identifying the context, e.g. "spotify:playlist:xxx" or "spotify:album:yyy".
    /// </summary>
    public required string ContextUri { get; init; }

    /// <summary>
    /// The type of playback context.
    /// </summary>
    public required PlaybackContextType Type { get; init; }

    /// <summary>
    /// Display name of the context (e.g. playlist name, album title).
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Cover image URL for the context.
    /// </summary>
    public string? ImageUrl { get; init; }
}

/// <summary>
/// Types of playback contexts.
/// </summary>
public enum PlaybackContextType
{
    Album,
    Playlist,
    Artist,
    LikedSongs,
    Queue,
    Search,
    Unknown
}
