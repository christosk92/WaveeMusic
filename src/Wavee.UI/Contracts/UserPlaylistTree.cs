using System.Collections.Generic;

namespace Wavee.UI.Contracts;

/// <summary>
/// Hierarchical view of the current user's rootlist: top-level children
/// in arrival order, folders carrying their own nested children, playlist
/// leaves carrying the full <see cref="Models.PlaylistSummaryDto"/> so callers
/// can read <c>IsOwner</c> / display name / cover without a second round trip.
///
/// Returned by <see cref="ILibraryDataService.GetUserPlaylistTreeAsync"/>.
/// Built by walking <c>Wavee.Core.Playlists.RootlistTreeBuilder.Build</c> over
/// the same rootlist snapshot the sidebar uses, so a folder you see in the
/// sidebar appears identically in every menu rendered against this tree.
/// </summary>
public sealed record UserPlaylistTree(IReadOnlyList<UserPlaylistTreeNode> Children);

public abstract record UserPlaylistTreeNode;

/// <summary>
/// A user-defined rootlist folder. <paramref name="Children"/> is in arrival
/// order — a playlist between two sub-folders stays between them.
/// </summary>
public sealed record UserPlaylistFolderNode(
    string Id,
    string Name,
    IReadOnlyList<UserPlaylistTreeNode> Children)
    : UserPlaylistTreeNode;

/// <summary>
/// A playlist leaf inside the rootlist tree. The DTO carries everything menus
/// need — id, display name, owner flag, collaborator flag, cover.
/// </summary>
public sealed record UserPlaylistLeafNode(Models.PlaylistSummaryDto Playlist)
    : UserPlaylistTreeNode;
