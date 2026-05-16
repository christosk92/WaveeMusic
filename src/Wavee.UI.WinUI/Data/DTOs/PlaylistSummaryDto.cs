namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// Summary of a playlist for display in sidebar/lists.
/// </summary>
public sealed record PlaylistSummaryDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? ImageUrl { get; init; }
    public int TrackCount { get; init; }
    public bool IsOwner { get; init; }

    /// <summary>True when the playlist is in Spotify's collaborative mode (any
    /// follower can edit). Sourced from <c>RootlistDecoration.IsCollaborative</c>.</summary>
    public bool IsCollaborative { get; init; }

    /// <summary>Derived gate for "can the current user mutate this playlist's tracks".
    /// True when the user owns it OR it is collaborative. Used by drag-drop drop
    /// predicates so non-editable rows reject add-tracks gestures.</summary>
    public bool CanEditItems => IsOwner || IsCollaborative;
}
