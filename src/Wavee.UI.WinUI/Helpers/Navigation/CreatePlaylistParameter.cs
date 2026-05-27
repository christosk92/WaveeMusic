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
    /// Optional sidebar folder to place the new playlist into. Must be the
    /// folder's <c>spotify:start-group:{id}:{name}</c> URI — same shape the
    /// rootlist drag-into-folder path uses. When set, the create flow runs
    /// CreatePlaylistAsync then MovePlaylistIntoFolderAsync as a two-step
    /// rootlist mutation. Ignored when <see cref="IsFolder"/> is true (you
    /// can't nest a folder via this path).
    /// </summary>
    public string? FolderStartUri { get; init; }

    /// <summary>
    /// Number of tracks to be added.
    /// </summary>
    public int TrackCount => TrackIds?.Count ?? 0;

    /// <summary>
    /// Whether there are tracks to add.
    /// </summary>
    public bool HasTracks => TrackCount > 0;
}