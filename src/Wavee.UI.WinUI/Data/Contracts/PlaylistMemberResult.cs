using System;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// One member of a collaborative playlist's permission list. Surfaced from
/// <c>GET /playlist-permission/v1/playlist/{id}/permission/members</c>. The
/// owner is included in this list when present (with <see cref="Role"/> =
/// <see cref="PlaylistMemberRole.Owner"/>).
/// </summary>
public sealed record PlaylistMemberResult
{
    /// <summary>Bare Spotify user id (no <c>spotify:user:</c> prefix).</summary>
    public required string UserId { get; init; }

    /// <summary>Username as Spotify reports it. Often equal to <see cref="UserId"/>
    /// for newer accounts; older accounts have a separate handle.</summary>
    public required string Username { get; init; }

    /// <summary>Resolved display name via <c>IUserProfileResolver</c>. Null while
    /// resolution is pending or when the user has no public profile.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Resolved profile picture URL. Null when not available.</summary>
    public string? AvatarUrl { get; init; }

    /// <summary>Permission role on this playlist.</summary>
    public required PlaylistMemberRole Role { get; init; }
}

/// <summary>
/// Permission role on a collaborative playlist. Mirrors Spotify's
/// <c>permissionLevel</c> enum; the additional <see cref="Owner"/> value is set
/// client-side for the owner row even though the server reports them via a
/// separate <c>ownerUsername</c> field rather than the members list.
/// </summary>
public enum PlaylistMemberRole
{
    Viewer,
    Contributor,
    Blocked,
    Owner
}

/// <summary>
/// Per-link invite produced by
/// <c>POST /playlist-permission/v1/playlist/{id}/permission-grant</c>. The
/// <see cref="ShareUrl"/> is composed locally from the playlist id and the
/// returned <see cref="Token"/>; sharing it grants whoever opens it the
/// <see cref="GrantedRole"/> for <see cref="Ttl"/>.
/// </summary>
public sealed record PlaylistInviteLink
{
    public required string Token { get; init; }
    public required string ShareUrl { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required TimeSpan Ttl { get; init; }
    public required PlaylistMemberRole GrantedRole { get; init; }
}
