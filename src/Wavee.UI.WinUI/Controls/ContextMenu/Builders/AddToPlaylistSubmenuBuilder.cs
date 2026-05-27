using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Controls.ContextMenu.Builders;

/// <summary>
/// Builds a folder-aware "Add to playlist" submenu shared between the track
/// context menu (single-track add) and the card context menu (resolve URI to
/// tracks via <see cref="Services.DragDrop.IPlaylistDragDropMediator"/> first).
///
/// Folders in the user's rootlist appear as nested rows; owned playlists
/// appear as leaves. Followed (non-owned) playlists are filtered out because
/// Spotify rejects writes to playlists you don't own. Empty folders (no owned
/// playlists transitively) are hidden — empty rows are pure noise. Cold-cache
/// fallback is a single "Create new playlist" entry.
/// </summary>
public static class AddToPlaylistSubmenuBuilder
{
    /// <summary>
    /// Returns a lazy loader suitable for <see cref="ContextMenuItemModel.LoadSubMenuAsync"/>.
    /// </summary>
    /// <param name="sourceLabel">
    /// Human label of the entity being added — used in the success toast
    /// ("Added 12 tracks to Workout"). Pass the track / album / playlist /
    /// show display name.
    /// </param>
    /// <param name="trackUrisLoader">
    /// Resolves the source entity to a flat list of Spotify track URIs. For a
    /// track menu this is a one-element constant; for card menus it routes
    /// through <c>IPlaylistDragDropMediator</c> (album → tracks, show → episodes,
    /// artist → top tracks, playlist → tracks, liked → all). The loader is
    /// invoked once per *click* — opening the submenu doesn't pay the cost.
    /// </param>
    public static Func<Task<IReadOnlyList<ContextMenuItemModel>>> Loader(
        string sourceLabel,
        Func<CancellationToken, Task<IReadOnlyList<string>>> trackUrisLoader)
    {
        ArgumentNullException.ThrowIfNull(trackUrisLoader);

        return async () =>
        {
            var library = Ioc.Default.GetService<ILibraryDataService>();
            var items = new List<ContextMenuItemModel>
            {
                new()
                {
                    Text = AppLocalization.GetString("TrackMenu_CreateNewPlaylist"),
                    Glyph = FluentGlyphs.CreatePlaylist,
                    Invoke = () => CreateNewPlaylistFromSource(trackUrisLoader)
                }
            };

            UserPlaylistTree? tree = null;
            if (library is not null)
            {
                try
                {
                    tree = await library.GetUserPlaylistTreeAsync().ConfigureAwait(true);
                }
                catch
                {
                    // Cold cache / signed-out / transient: degrade to the
                    // single "Create new playlist" entry rather than ship a
                    // broken submenu. The toast on click still fires.
                    tree = null;
                }
            }

            if (tree is { Children: { Count: > 0 } children })
            {
                var nodeItems = BuildNodes(children, sourceLabel, trackUrisLoader);
                if (nodeItems.Count > 0)
                {
                    items.Add(ContextMenuItemModel.Separator);
                    items.AddRange(nodeItems);
                }
            }

            return items;
        };
    }

    private static List<ContextMenuItemModel> BuildNodes(
        IReadOnlyList<UserPlaylistTreeNode> children,
        string sourceLabel,
        Func<CancellationToken, Task<IReadOnlyList<string>>> trackUrisLoader)
    {
        var output = new List<ContextMenuItemModel>(children.Count);
        foreach (var node in children)
        {
            switch (node)
            {
                case UserPlaylistFolderNode folder:
                {
                    // Every folder is always actionable: we prepend
                    // "Create new playlist in this folder…" so even folders
                    // containing only followed (non-addable) playlists give
                    // the user a meaningful action. Owned playlists below
                    // are still clickable; followed playlists are filtered
                    // out by the leaf branch.
                    var folderStartUri = BuildFolderStartUri(folder);
                    var folderChildren = new List<ContextMenuItemModel>
                    {
                        new()
                        {
                            Text = AppLocalization.GetString("TrackMenu_CreateNewPlaylist"),
                            Glyph = FluentGlyphs.CreatePlaylist,
                            Invoke = () => CreateNewPlaylistInFolder(folderStartUri, trackUrisLoader)
                        }
                    };
                    var ownedChildren = BuildNodes(folder.Children, sourceLabel, trackUrisLoader);
                    if (ownedChildren.Count > 0)
                    {
                        folderChildren.Add(ContextMenuItemModel.Separator);
                        folderChildren.AddRange(ownedChildren);
                    }
                    output.Add(new ContextMenuItemModel
                    {
                        Text = folder.Name,
                        Glyph = FluentGlyphs.Folder,
                        Items = folderChildren
                    });
                    break;
                }

                case UserPlaylistLeafNode leaf:
                    if (!leaf.Playlist.IsOwner) continue;
                    var pid = leaf.Playlist.Id;
                    var pname = leaf.Playlist.Name;
                    output.Add(new ContextMenuItemModel
                    {
                        Text = pname,
                        Glyph = FluentGlyphs.Playlist,
                        Invoke = () => _ = AddToPlaylistAsync(pid, pname, sourceLabel, trackUrisLoader)
                    });
                    break;
            }
        }
        return output;
    }

    /// <summary>
    /// Reconstructs the canonical <c>spotify:start-group:{id}:{encoded-name}</c>
    /// URI for a rootlist folder. Mirrors the encoding the create path uses
    /// (<see cref="Wavee.UI.WinUI.Data.Contexts.RootlistService.CreateFolderAsync"/>):
    /// URL-escape with <c>+</c> instead of <c>%20</c> for spaces. Required by
    /// <see cref="IRootlistService.MovePlaylistIntoFolderAsync"/>.
    /// </summary>
    private static string BuildFolderStartUri(UserPlaylistFolderNode folder)
    {
        var encodedName = Uri.EscapeDataString(folder.Name ?? string.Empty)
            .Replace("%20", "+", StringComparison.Ordinal);
        return $"spotify:start-group:{folder.Id}:{encodedName}";
    }

    private static async void CreateNewPlaylistInFolder(
        string folderStartUri,
        Func<CancellationToken, Task<IReadOnlyList<string>>> trackUrisLoader)
    {
        IReadOnlyList<string> uris;
        try
        {
            uris = await trackUrisLoader(CancellationToken.None).ConfigureAwait(true);
        }
        catch
        {
            uris = Array.Empty<string>();
        }
        NavigationHelpers.OpenCreatePlaylist(
            isFolder: false,
            trackIds: uris,
            folderStartUri: folderStartUri);
    }

    private static async Task AddToPlaylistAsync(
        string playlistId,
        string playlistName,
        string sourceLabel,
        Func<CancellationToken, Task<IReadOnlyList<string>>> trackUrisLoader)
    {
        var mutations = Ioc.Default.GetService<IPlaylistMutationService>();
        var notifications = Ioc.Default.GetService<INotificationService>();
        if (mutations is null) return;

        IReadOnlyList<string> uris;
        try
        {
            uris = await trackUrisLoader(CancellationToken.None).ConfigureAwait(true);
        }
        catch
        {
            notifications?.Show(
                $"Couldn't load tracks from {(string.IsNullOrEmpty(sourceLabel) ? "source" : sourceLabel)}",
                NotificationSeverity.Error,
                TimeSpan.FromSeconds(3));
            return;
        }

        if (uris.Count == 0)
        {
            notifications?.Show(
                $"Nothing to add from {(string.IsNullOrEmpty(sourceLabel) ? "source" : sourceLabel)}",
                NotificationSeverity.Informational,
                TimeSpan.FromSeconds(3));
            return;
        }

        try
        {
            // Mutation service chunks at 500 per Op internally (per the
            // /playlist/v2 wire contract). Pass the full list — don't second-
            // guess.
            await mutations.AddTracksToPlaylistAsync(playlistId, uris).ConfigureAwait(true);
            var noun = uris.Count == 1 ? "track" : "tracks";
            notifications?.Show(
                string.IsNullOrEmpty(playlistName)
                    ? $"Added {uris.Count} {noun} to playlist"
                    : $"Added {uris.Count} {noun} to {playlistName}",
                NotificationSeverity.Success,
                TimeSpan.FromSeconds(3));
        }
        catch
        {
            notifications?.Show(
                string.IsNullOrEmpty(playlistName)
                    ? "Couldn't add to the playlist"
                    : $"Couldn't add to {playlistName}",
                NotificationSeverity.Error,
                TimeSpan.FromSeconds(3));
        }
    }

    private static async void CreateNewPlaylistFromSource(
        Func<CancellationToken, Task<IReadOnlyList<string>>> trackUrisLoader)
    {
        IReadOnlyList<string> uris;
        try
        {
            uris = await trackUrisLoader(CancellationToken.None).ConfigureAwait(true);
        }
        catch
        {
            uris = Array.Empty<string>();
        }
        NavigationHelpers.OpenCreatePlaylist(isFolder: false, trackIds: uris);
    }
}
