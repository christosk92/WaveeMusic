using System.Collections.Generic;

namespace Wavee.UI.WinUI.Helpers.Navigation;

/// <summary>
/// Parameter for creating a new playlist, optionally with pre-selected tracks.
/// </summary>
public sealed record CreatePlaylistParameter
{
    /// <summary>
    /// Whether to create a folder instead of a playlist.
    /// </summary>
    public bool IsFolder { get; init; }

    /// <summary>
    /// Optional track IDs to add to the playlist after creation.
    /// </summary>
    public IReadOnlyList<string>? TrackIds { get; init; }

    /// <summary>
    /// Number of tracks to be added.
    /// </summary>
    public int TrackCount => TrackIds?.Count ?? 0;

    /// <summary>
    /// Whether there are tracks to add.
    /// </summary>
    public bool HasTracks => TrackCount > 0;
}
