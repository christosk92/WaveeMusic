namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// One Wavee-only overlay row attached to a Spotify playlist. Track URI is
/// always a <c>wavee:local:track:*</c> URI today; the column is generic for
/// future kinds. Position is the merged-view position used to interleave
/// with Spotify rows during rendering.
/// </summary>
public sealed record PlaylistOverlayRow(
    string TrackUri,
    int Position,
    long AddedAt,
    string? AddedBy);
