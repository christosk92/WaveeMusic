namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// Mirror of Spotify's playlist <c>basePermission</c>. Determines the role the
/// current user has on the playlist before any per-capability overrides are applied.
/// </summary>
public enum PlaylistBasePermission
{
    Viewer,
    Contributor,
    Owner
}

/// <summary>
/// Mirror of Spotify's <c>currentUserCapabilities</c> object. Per-action booleans
/// the server tells us about; we just pass them through and use them for UI gating.
/// </summary>
public sealed record PlaylistCapabilitiesDto
{
    public bool CanView { get; init; } = true;
    public bool CanEditItems { get; init; }
    public bool CanAdministratePermissions { get; init; }
    public bool CanCancelMembership { get; init; }
    public bool CanAbuseReport { get; init; }

    /// <summary>Default view-only capability set used when the server hasn't provided one.</summary>
    public static PlaylistCapabilitiesDto ViewOnly { get; } = new();
}

/// <summary>
/// Represents detailed playlist metadata.
/// </summary>
public sealed record PlaylistDetailDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? ImageUrl { get; init; }
    /// <summary>
    /// Wide editorial hero image (from the <c>header_image_url_desktop</c> playlist
    /// format attribute). Present on editorial / radio playlists only. The page
    /// renders this instead of the square cover when non-null.
    /// </summary>
    public string? HeaderImageUrl { get; init; }
    public required string OwnerName { get; init; }
    public string? OwnerId { get; init; }
    public int TrackCount { get; init; }
    public int FollowerCount { get; init; }
    public bool IsOwner { get; init; }
    public bool IsCollaborative { get; init; }
    public bool IsPublic { get; init; }
    public PlaylistBasePermission BasePermission { get; init; } = PlaylistBasePermission.Viewer;
    public PlaylistCapabilitiesDto Capabilities { get; init; } = PlaylistCapabilitiesDto.ViewOnly;

    /// <summary>
    /// Playlist-level format attributes returned by the playlist service —
    /// editorial + recommender chrome (<c>format</c>, <c>request_id</c>,
    /// <c>tag</c>, <c>source-loader</c>, <c>image_url</c>,
    /// <c>session_control_display.displayName.*</c>, etc.). Forwarded into
    /// <c>PlayerState.context_metadata</c> at play time. Null/empty for
    /// user-authored playlists.
    /// </summary>
    public System.Collections.Generic.IReadOnlyDictionary<string, string>? FormatAttributes { get; init; }
}
