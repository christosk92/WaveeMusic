using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Controls.ContextMenu.Builders;

/// <summary>
/// Builds a folder-aware "Save to Your Library" submenu for the playlist card
/// context menu. Lets the user pick whether the followed playlist lands at the
/// top of the sidebar or inside a specific folder. Mirrors the rootlist tree
/// shape used by <see cref="AddToPlaylistSubmenuBuilder"/>: empty / flat
/// rootlists collapse cleanly, nested folders render recursively.
/// </summary>
public static class SaveToLibrarySubmenuBuilder
{
    /// <summary>
    /// Returns a lazy loader suitable for <see cref="ContextMenuItemModel.LoadSubMenuAsync"/>.
    /// </summary>
    /// <param name="playlistUri">
    /// The <c>spotify:playlist:{id}</c> URI to follow + (optionally) place into
    /// a folder. Picking "Top of library" runs only the follow step; picking a
    /// folder follows then moves the playlist into that folder's start-group.
    /// </param>
    /// <param name="playlistName">
    /// Display name — used only in error toasts ("Couldn't save <name>").
    /// </param>
    public static Func<Task<IReadOnlyList<ContextMenuItemModel>>> Loader(
        string playlistUri,
        string playlistName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistUri);

        return async () =>
        {
            var items = new List<ContextMenuItemModel>
            {
                new()
                {
                    Text = AppLocalization.GetString("PlaylistMenu_SaveToLibrary_TopOfLibrary"),
                    Glyph = FluentGlyphs.HeartOutline,
                    Invoke = () => _ = SaveAsync(playlistUri, playlistName, folderStartUri: null)
                }
            };

            var library = Ioc.Default.GetService<ILibraryDataService>();
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
                    // single "Top of library" entry rather than ship a broken
                    // submenu.
                    tree = null;
                }
            }

            if (tree is { Children: { Count: > 0 } children })
            {
                var folderItems = BuildFolderRows(children, playlistUri, playlistName);
                if (folderItems.Count > 0)
                {
                    items.Add(ContextMenuItemModel.Separator);
                    items.AddRange(folderItems);
                }
            }

            return items;
        };
    }

    /// <summary>
    /// Walks the rootlist tree and emits one row per folder. Playlist leaves
    /// are ignored — Save-to-Library targets are folders only. A folder with
    /// nested sub-folders becomes a sub-flyout with "Save here" at the top so
    /// the user can still target the outer folder; a folder with no nested
    /// folders renders as a single clickable row.
    /// </summary>
    private static List<ContextMenuItemModel> BuildFolderRows(
        IReadOnlyList<UserPlaylistTreeNode> nodes,
        string playlistUri,
        string playlistName)
    {
        var output = new List<ContextMenuItemModel>();
        foreach (var node in nodes)
        {
            if (node is not UserPlaylistFolderNode folder) continue;

            var folderStartUri = BuildFolderStartUri(folder);
            var nestedFolders = BuildFolderRows(folder.Children, playlistUri, playlistName);

            if (nestedFolders.Count == 0)
            {
                output.Add(new ContextMenuItemModel
                {
                    Text = folder.Name,
                    Glyph = FluentGlyphs.Folder,
                    Invoke = () => _ = SaveAsync(playlistUri, playlistName, folderStartUri)
                });
            }
            else
            {
                var subItems = new List<ContextMenuItemModel>
                {
                    new()
                    {
                        Text = AppLocalization.GetString("PlaylistMenu_SaveToLibrary_TopOfLibrary"),
                        Glyph = FluentGlyphs.Folder,
                        Invoke = () => _ = SaveAsync(playlistUri, playlistName, folderStartUri)
                    },
                    ContextMenuItemModel.Separator
                };
                subItems.AddRange(nestedFolders);

                output.Add(new ContextMenuItemModel
                {
                    Text = folder.Name,
                    Glyph = FluentGlyphs.Folder,
                    Items = subItems
                });
            }
        }
        return output;
    }

    /// <summary>
    /// Reconstructs the canonical <c>spotify:start-group:{id}:{encoded-name}</c>
    /// URI for a rootlist folder. Mirrors the encoding the create path uses
    /// (URL-escape with <c>+</c> for spaces). Required by
    /// <see cref="IRootlistService.MovePlaylistIntoFolderAsync"/>.
    /// </summary>
    private static string BuildFolderStartUri(UserPlaylistFolderNode folder)
    {
        var encodedName = Uri.EscapeDataString(folder.Name ?? string.Empty)
            .Replace("%20", "+", StringComparison.Ordinal);
        return $"spotify:start-group:{folder.Id}:{encodedName}";
    }

    private static async Task SaveAsync(string playlistUri, string playlistName, string? folderStartUri)
    {
        var mutations = Ioc.Default.GetService<IPlaylistMutationService>();
        var rootlist = Ioc.Default.GetService<IRootlistService>();
        var notifications = Ioc.Default.GetService<INotificationService>();
        if (mutations is null) return;

        try
        {
            await mutations.SetPlaylistFollowedAsync(playlistUri, followed: true).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(folderStartUri) && rootlist is not null)
            {
                await rootlist.MovePlaylistIntoFolderAsync(playlistUri, folderStartUri).ConfigureAwait(true);
            }

            notifications?.Show(
                string.IsNullOrEmpty(playlistName)
                    ? "Saved to library"
                    : $"Saved {playlistName} to library",
                NotificationSeverity.Success,
                TimeSpan.FromSeconds(3));
        }
        catch
        {
            notifications?.Show(
                string.IsNullOrEmpty(playlistName)
                    ? "Couldn't save to library"
                    : $"Couldn't save {playlistName}",
                NotificationSeverity.Error,
                TimeSpan.FromSeconds(3));
        }
    }
}
