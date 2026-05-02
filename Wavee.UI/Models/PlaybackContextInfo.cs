using System.Collections.Generic;

namespace Wavee.UI.Models;

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

    /// <summary>
    /// Context-level format attributes returned by the playlist service —
    /// <c>format</c>, <c>request_id</c>, <c>tag</c>, <c>source-loader</c>,
    /// <c>image_url</c>, <c>session_control_display.displayName.*</c>, etc.
    /// Forwarded into <c>PlayerState.context_metadata</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? FormatAttributes { get; init; }
}

/// <summary>
/// Types of playback contexts.
/// </summary>
public enum PlaybackContextType
{
    Album,
    Playlist,
    Artist,
    Show,
    Episode,
    LikedSongs,
    Queue,
    Search,
    Unknown
}
