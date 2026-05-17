using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// Playlist collaborator + invite-link surface. Carved out of
/// <c>ILibraryDataService</c> in Phase 2. Most methods are stubs today —
/// the wire-up to <c>SpClient</c>'s permission-grant endpoints is pending,
/// but the surface exists so the UI can bind against the final shape.
/// </summary>
public interface IPlaylistPermissionService
{
    /// <summary>
    /// Returns the members + their effective roles for the given playlist.
    /// Empty list on failure (logged at debug).
    /// </summary>
    Task<IReadOnlyList<PlaylistMemberResult>> GetPlaylistMembersAsync(
        string playlistId, CancellationToken ct = default);

    /// <summary>
    /// Changes a member's role (Owner / Contributor / Viewer / Blocked).
    /// Stub today — logs the call and returns completed.
    /// </summary>
    Task SetPlaylistMemberRoleAsync(
        string playlistId, string memberUserId, PlaylistMemberRole role,
        CancellationToken ct = default);

    /// <summary>
    /// Removes a member from a playlist's collaborator list.
    /// Stub today — logs and returns.
    /// </summary>
    Task RemovePlaylistMemberAsync(
        string playlistId, string memberUserId, CancellationToken ct = default);

    /// <summary>
    /// Creates a time-limited invite link that grants the supplied role to
    /// anyone who clicks it. Stub today — returns a synthetic link the UI
    /// can render against.
    /// </summary>
    Task<PlaylistInviteLink> CreatePlaylistInviteLinkAsync(
        string playlistId, PlaylistMemberRole grantedRole, TimeSpan ttl,
        CancellationToken ct = default);

    /// <summary>
    /// Flips the playlist's <c>collaborative</c> flag. Stub today.
    /// </summary>
    Task SetPlaylistCollaborativeAsync(
        string playlistId, bool collaborative, CancellationToken ct = default);
}
