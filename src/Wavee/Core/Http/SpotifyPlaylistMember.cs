namespace Wavee.Core.Http;

/// <summary>
/// One row returned by
/// <c>GET https://spclient.wg.spotify.com/playlist-permission/v1/playlist/{id}/permission/members</c>.
/// The endpoint's exact shape isn't publicly documented; this record holds
/// only the fields the desktop client's permission-management UI needs. Owner
/// rows are sometimes reported via a parallel <c>ownerUsername</c> field rather
/// than the members array — <see cref="ISpClient.GetPlaylistMembersAsync"/>
/// synthesises an Owner row in that case.
/// </summary>
public sealed record SpotifyPlaylistMember
{
    public required string UserId { get; init; }
    public required string Username { get; init; }
    public required string PermissionLevel { get; init; }
    public string? DisplayName { get; init; }
    public string? ImageUrl { get; init; }
}
