using System;
using System.Security.Cryptography;
using Wavee.Core.Playlists;

namespace Wavee.UI.WinUI.Data.Contexts.Helpers;

/// <summary>
/// Index- and span-walking helpers for the user's rootlist (playlist tree).
/// Shared between <see cref="RootlistService"/> (folder + move ops) and
/// <see cref="LibraryDataService"/> (delete-playlist + follow paths still
/// touch the rootlist directly).
/// </summary>
internal static class RootlistGraph
{
    /// <summary>
    /// Locates a rootlist playlist entry by URI. Returns the items-array
    /// index or -1.
    /// </summary>
    public static int FindRootlistPlaylistIndex(RootlistSnapshot rootlist, string playlistUri)
    {
        for (var i = 0; i < rootlist.Items.Count; i++)
        {
            if (rootlist.Items[i] is RootlistPlaylist playlist
                && string.Equals(playlist.Uri, playlistUri, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Locates a rootlist folder entry. Accepts both forms the sidebar uses:
    /// <c>spotify:start-group:{id}[:{name}]</c> (the real wire URI) and
    /// <c>folder:{id}</c> (the sidebar's navigation tag). Matches on the 16-hex
    /// id so cosmetic renames don't break callers that captured the URI earlier.
    /// </summary>
    public static int FindRootlistFolderStartIndex(RootlistSnapshot rootlist, string folderStartUri)
    {
        var folderId = folderStartUri switch
        {
            _ when folderStartUri.StartsWith("spotify:start-group:", StringComparison.OrdinalIgnoreCase) =>
                folderStartUri.Split(':') is { Length: >= 3 } parts ? parts[2] : null,
            _ when folderStartUri.StartsWith("folder:", StringComparison.OrdinalIgnoreCase) =>
                folderStartUri["folder:".Length..],
            _ => null,
        };
        if (string.IsNullOrEmpty(folderId)) return -1;

        for (var i = 0; i < rootlist.Items.Count; i++)
        {
            if (rootlist.Items[i] is RootlistFolderStart fs
                && string.Equals(fs.Id, folderId, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Given the index of a <see cref="RootlistFolderStart"/>, walks forward to
    /// the matching <see cref="RootlistFolderEnd"/> (same id, accounting for
    /// nested folders that share neither id nor span). Returns <c>-1</c> if
    /// no end marker is found.
    /// </summary>
    public static int FindMatchingFolderEndIndex(RootlistSnapshot rootlist, int startIndex)
    {
        if (startIndex < 0 || startIndex >= rootlist.Items.Count) return -1;
        if (rootlist.Items[startIndex] is not RootlistFolderStart start) return -1;

        var depth = 1;
        for (var i = startIndex + 1; i < rootlist.Items.Count; i++)
        {
            switch (rootlist.Items[i])
            {
                case RootlistFolderStart:
                    depth++;
                    break;
                case RootlistFolderEnd end when string.Equals(end.Id, start.Id, StringComparison.OrdinalIgnoreCase):
                    if (depth == 1) return i;
                    depth--;
                    break;
            }
        }
        return -1;
    }

    /// <summary>
    /// Resolves a URI to a rootlist index — folder or playlist.
    /// </summary>
    public static int FindRootlistEntryIndex(RootlistSnapshot rootlist, string uri)
    {
        if (uri.StartsWith("spotify:start-group:", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("folder:", StringComparison.OrdinalIgnoreCase))
            return FindRootlistFolderStartIndex(rootlist, uri);
        return FindRootlistPlaylistIndex(rootlist, uri);
    }

    /// <summary>
    /// Length of the rootlist entry at <paramref name="index"/> when treated as
    /// a movable block: 1 for a bare playlist, span (end - start + 1) for a folder.
    /// </summary>
    public static int RootlistEntrySpan(RootlistSnapshot rootlist, int index)
    {
        if (index < 0 || index >= rootlist.Items.Count) return 0;
        if (rootlist.Items[index] is RootlistFolderStart)
        {
            var endIdx = FindMatchingFolderEndIndex(rootlist, index);
            return endIdx >= 0 ? endIdx - index + 1 : 1;
        }
        return 1;
    }

    /// <summary>
    /// Builds the <see cref="Wavee.Protocol.Playlist.ChangeInfo"/> stamp every
    /// rootlist mutation needs (user + ms timestamp).
    /// </summary>
    public static Wavee.Protocol.Playlist.ChangeInfo BuildRootlistChangeInfo(string username, long timestampMs)
        => new() { User = username, Timestamp = timestampMs };

    /// <summary>
    /// Random server-side dedup token. Captured first-party body uses a small
    /// integer; a random 31-bit value avoids monotonicity assumptions.
    /// </summary>
    public static long NextRootlistNonce()
        => RandomNumberGenerator.GetInt32(1, int.MaxValue);
}
