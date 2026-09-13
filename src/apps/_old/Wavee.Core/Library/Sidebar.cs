namespace Wavee.Core;

// The sidebar's information-architecture data — the "Your Library" counts and the (folder-capable) playlist tree.
// Framework-neutral, driven by FakeData; the UI binds these instead of hard-coded literal arrays.

/// <summary>A lightweight playlist row for the sidebar (no track payload — the detail page loads that on demand).
/// <paramref name="MosaicTiles"/> (when <paramref name="Cover"/> is null) carries up to 4 distinct album-cover URLs to
/// compose a 2×2 mosaic, the way Spotify renders a cover-less playlist; recomputed from the live tracklist.</summary>
public sealed record PlaylistSummary(string Uri, string Name, string OwnerName, int TrackCount, Image? Cover,
    System.Collections.Generic.IReadOnlyList<string>? MosaicTiles = null, bool CanEdit = false, bool IsOwner = false,
    // Daylist rollover window (unix ms) when the producing surface knew it (the Home card's Pathfinder attributes);
    // 0 elsewhere. Rides the click→detail preview so the countdown paints with the header.
    long DaylistExpiresAtMs = 0, long DaylistCreatedAtMs = 0,
    // Server extractedColors.colorDark as opaque ARGB (0 = none). The Home card already has this before any image
    // grades; the detail page's CoverColorPlane lookup often misses a daylist cover, so the payload rides the
    // click→detail preview the same way the rollover window does.
    uint Accent = 0);

/// <summary>A node in the sidebar playlist tree: either a single playlist or a folder of playlists (WaveeMusic's
/// hierarchical Playlists section — flat leaves + collapsible folders).</summary>
public abstract record PlaylistNode;
public sealed record PlaylistLeaf(PlaylistSummary Playlist) : PlaylistNode;

/// <summary>A folder in the playlist tree. <see cref="Items"/> are NODES, not summaries — folders are RECURSIVE
/// (Spotify's real rootlist model: a folder can contain folders). Flat consumers (the "Add to playlist" picker, the
/// Classic sidebar list, <c>LibraryStore.Playlists</c>) must go through <see cref="SidebarTree.Flatten(IReadOnlyList{PlaylistNode})"/>
/// so a leaf at ANY depth still reaches them — see the FlatConsumers_StillSeeEveryPlaylist regression test.</summary>
public sealed record PlaylistFolder(string Id, string Name, IReadOnlyList<PlaylistNode> Items) : PlaylistNode;

/// <summary>The playlist-tree helpers shared by the tree's producers (the rootlist builder, the fakes) and by every
/// FLAT consumer that predates recursive folders. Framework-neutral, allocation-conscious (the <c>List</c> overloads let
/// a caller reuse its buffer).</summary>
public static class SidebarTree
{
    /// <summary>Nesting depth the walkers refuse to go past — a malformed marker stream can only ever produce a finite
    /// tree, but the cap keeps a pathological rootlist from turning into deep recursion.</summary>
    public const int MaxDepth = 32;

    /// <summary>The canonical empty added-at side-channel (see <c>IMusicLibrary.GetLibraryAddedAtAsync</c>).</summary>
    public static readonly IReadOnlyDictionary<string, long> NoAddedAt =
        new System.Collections.Generic.Dictionary<string, long>(0, System.StringComparer.Ordinal);

    /// <summary>Every playlist in the tree, at EVERY depth, in tree order (folders expanded in place). This is the one
    /// bridge that keeps the pre-recursion flat consumers correct.</summary>
    public static IReadOnlyList<PlaylistSummary> Flatten(IReadOnlyList<PlaylistNode> tree)
    {
        var into = new System.Collections.Generic.List<PlaylistSummary>(tree.Count);
        Flatten(tree, into);
        return into;
    }

    /// <summary>Append-into overload (the caller owns + reuses the list; it is NOT cleared).</summary>
    public static void Flatten(IReadOnlyList<PlaylistNode> tree, System.Collections.Generic.List<PlaylistSummary> into)
        => FlattenAt(tree, into, 0);

    static void FlattenAt(IReadOnlyList<PlaylistNode> nodes, System.Collections.Generic.List<PlaylistSummary> into, int depth)
    {
        if (depth > MaxDepth) return;
        for (int i = 0; i < nodes.Count; i++)
        {
            switch (nodes[i])
            {
                case PlaylistLeaf leaf: into.Add(leaf.Playlist); break;
                case PlaylistFolder folder: FlattenAt(folder.Items, into, depth + 1); break;
            }
        }
    }

    /// <summary>A flat playlist list as a leaves-only tree — the shape a source with no folder markers reports.</summary>
    public static IReadOnlyList<PlaylistNode> FromFlat(IReadOnlyList<PlaylistSummary> playlists)
    {
        var nodes = new PlaylistNode[playlists.Count];
        for (int i = 0; i < playlists.Count; i++) nodes[i] = new PlaylistLeaf(playlists[i]);
        return nodes;
    }

    /// <summary>Playlist count at every depth (the badge/count consumers' number).</summary>
    public static int CountLeaves(IReadOnlyList<PlaylistNode> tree) => CountLeavesAt(tree, 0);

    static int CountLeavesAt(IReadOnlyList<PlaylistNode> nodes, int depth)
    {
        if (depth > MaxDepth) return 0;
        int n = 0;
        for (int i = 0; i < nodes.Count; i++)
            n += nodes[i] switch
            {
                PlaylistLeaf => 1,
                PlaylistFolder f => CountLeavesAt(f.Items, depth + 1),
                _ => 0,
            };
        return n;
    }

    /// <summary>Depth-first search for a folder by its rootlist group id (null = absent).</summary>
    public static PlaylistFolder? FindFolder(IReadOnlyList<PlaylistNode> tree, string folderId) => FindFolderAt(tree, folderId, 0);

    static PlaylistFolder? FindFolderAt(IReadOnlyList<PlaylistNode> nodes, string folderId, int depth)
    {
        if (depth > MaxDepth) return null;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is not PlaylistFolder f) continue;
            if (string.Equals(f.Id, folderId, System.StringComparison.Ordinal)) return f;
            if (FindFolderAt(f.Items, folderId, depth + 1) is { } hit) return hit;
        }
        return null;
    }
}

/// <summary>The "Your Library" badge counts (Albums / Artists / Liked Songs / Podcasts).</summary>
public sealed record LibraryStats(int Albums, int Artists, int LikedSongs, int Podcasts);
