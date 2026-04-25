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
///
/// <para>
/// Per-attribute flags (<see cref="CanEditName"/>, <see cref="CanEditDescription"/>,
/// <see cref="CanEditPicture"/>, <see cref="CanEditCollaborative"/>) shadow the
/// server's <c>listAttributeCapabilities.{name|description|picture|collaborative}.canEdit</c>
/// fields. Today the cache layer doesn't surface those individually, so each one
/// is derived from <see cref="CanEditMetadata"/>; when fine-grained data starts
/// flowing through the cache, the derivation can become a real per-field map.
/// </para>
/// </summary>
public sealed record PlaylistCapabilitiesDto
{
    public bool CanView { get; init; } = true;
    public bool CanEditItems { get; init; }
    public bool CanEditMetadata { get; init; }
    public bool CanDelete { get; init; }
    public bool CanAdministratePermissions { get; init; }
    public bool CanCancelMembership { get; init; }
    public bool CanAbuseReport { get; init; }

    public bool CanEditName => CanEditMetadata;
    public bool CanEditDescription => CanEditMetadata;
    public bool CanEditPicture => CanEditMetadata;
    public bool CanEditCollaborative => CanEditMetadata;

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

    /// <summary>
    /// Current 24-byte playlist revision. Needed by endpoints that require
    /// the caller to quote the revision they're acting against (e.g. the
    /// playlist signals endpoint). Empty for cache misses.
    /// </summary>
    public byte[]? Revision { get; init; }

    /// <summary>
    /// Pre-parsed session-control-display chip options derived from
    /// <see cref="FormatAttributes"/>. Null when the playlist has no
    /// session-control chrome (the common case for user-authored lists).
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<SessionControlOption>? SessionControlOptions { get; init; }

    /// <summary>
    /// Opaque control-group id for the session-control chip row. Joined into
    /// the signal key as <c>session_control_display$&lt;group_id&gt;$&lt;option&gt;</c>.
    /// Null when <see cref="SessionControlOptions"/> is also null.
    /// </summary>
    public string? SessionControlGroupId { get; init; }
}

/// <summary>
/// One chip in the session-control-display row — e.g. "Pop Rock", "K-Ballad".
/// </summary>
public sealed record SessionControlOption
{
    /// <summary>Raw option key from the format attribute (e.g. <c>pop_rock</c>).</summary>
    public required string OptionKey { get; init; }

    /// <summary>Display label Spotify wants us to render (e.g. <c>Pop Rock</c>).</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Fully-formed signal identifier to POST on click (e.g.
    /// <c>session_control_display$24pGOSaKeoU6bobuwqnMbJ$pop</c>). Pulled
    /// directly from <c>SelectedListContent.Contents.AvailableSignals</c> by
    /// matching the entry whose suffix is <c>$&lt;OptionKey&gt;</c>. Null when
    /// the server didn't advertise a signal for this option — click is
    /// disabled in that case.
    /// </summary>
    public string? SignalIdentifier { get; init; }
}
