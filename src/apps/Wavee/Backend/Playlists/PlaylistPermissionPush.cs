using Wavee.Core;

namespace Wavee.Backend.Playlists;

/// <summary>A decoded <c>hm://playlist-permission/v1/playlist/{id}/permission/state</c> dealer push
/// (<c>PermissionStatePub</c>). This IS the authoritative permission state: a resident header adopts it with no HTTP GET
/// at all, which is why the base permission is mandatory on the wire (a push without one is a logged drop).
/// <para><see cref="RevisionHex"/> is the permission-chain revision as lowercase hex — deliberately NOT a playlist4
/// revision (it is 8 bytes or the "default" sentinel) and therefore never enters a membership/rootlist revision slot.</para></summary>
public sealed record PlaylistPermissionPush(
    string Uri,
    PlaylistPermissionLevel Level,
    string RevisionHex,
    bool IsPrivate,
    bool IsCollaborative);
