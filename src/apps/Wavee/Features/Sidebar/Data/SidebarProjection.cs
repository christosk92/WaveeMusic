using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Wavee.Core;

namespace Wavee;

// The unified sidebar projection (F.7.5) — LibraryStore's warm cells + HistoryStore recency + the added-at side-channel
// + the first-seen stamps, flattened into ONE linear entry list every design renders.
//
// Engine-free (System + Wavee.Core only), source-included by src/apps/Wavee.Tests.
//
// ALLOCATION DISCIPLINE: the CALLER owns the List (a UseRef) and reuses it across rebuilds, so a steady-state rebuild
// allocates nothing but the occasional list growth. No LINQ, no closures, no per-row lambdas — index loops only. Build is
// called from a UseMemo keyed on a DepKey over the input versions; it is NOT a per-frame call.

/// <summary>What a <see cref="SidebarProjection.Build"/> pass produced: how many rows, which playlist flavors were seen
/// (a bitset over <see cref="SidebarPlaylistFlavor"/>), and how many FRESH first-seen stamps were recorded (a non-zero
/// value is what makes the owner persist the document — commit point #9).</summary>
public readonly record struct SidebarProjectionResult(int Count, byte FlavorMask, int NewFirstSeenStamps);

public static class SidebarProjection
{
    // UI-thread only, like everything else in this layer. Used to join an album's artist names without a per-row
    // string[]/LINQ; the common single-artist case never touches it.
    static readonly StringBuilder s_join = new(64);

    /// <summary>
    /// Fill <paramref name="into"/> (CLEARED first) with the unified entry list for the requested kinds.
    ///
    /// Emission order is SOURCE order — the rootlist tree in rootlist order, then albums, artists, shows in the order
    /// their collections already carry (the store serves them newest-saved first). Sorting is a separate step
    /// (<see cref="SidebarSort"/>), and the pins-first partition is a third (<see cref="PinsFirst"/>); keeping them apart
    /// is what lets one projection serve five sorts and three designs.
    /// </summary>
    /// <param name="includeFolderChildren">false ⇒ folders are OPAQUE rows (V3 tree mode: a collapsed folder's children
    /// are not emitted); true ⇒ the tree is fully flattened (what a search or a flat sort needs).</param>
    /// <param name="isFolderExpanded">Optional per-folder override consulted only when
    /// <paramref name="includeFolderChildren"/> is false: an EXPANDED folder's children are emitted directly after it.
    /// Null ⇒ every folder is collapsed.</param>
    public static SidebarProjectionResult Build(
        List<SidebarLibraryEntry> into,
        SidebarEntryKindMask kinds,
        IReadOnlyList<PlaylistNode> playlistTree,
        IReadOnlyList<Album> albums,
        IReadOnlyList<Artist> artists,
        IReadOnlyList<Show> shows,
        IReadOnlyDictionary<string, long>? addedAt,
        SidebarRecency? recency,
        SidebarFirstSeen? firstSeen,
        bool includeFolderChildren,
        Func<string, bool>? isFolderExpanded = null)
    {
        into.Clear();
        var rec = recency ?? SidebarRecency.Empty;
        var seen = firstSeen ?? SidebarFirstSeen.Frozen;
        var added0 = addedAt ?? SidebarTree.NoAddedAt;
        int stampsBefore = seen.NewStamps;
        byte flavorMask = 0;

        bool wantPlaylists = (kinds & SidebarEntryKindMask.Playlist) != 0;
        bool wantFolders = (kinds & SidebarEntryKindMask.Folder) != 0;
        if ((wantPlaylists || wantFolders) && playlistTree.Count > 0)
        {
            int order = 0;
            Walk(playlistTree, into, "", "", 0, ref order, ref flavorMask,
                 wantPlaylists, wantFolders, includeFolderChildren, isFolderExpanded, added0, rec, seen);
        }

        if ((kinds & SidebarEntryKindMask.Album) != 0)
            for (int i = 0; i < albums.Count; i++)
            {
                var al = albums[i];
                string id = SidebarPinId.AlbumPrefix + al.Uri;
                long added = Added(added0, al.Uri);
                into.Add(new SidebarLibraryEntry(
                    id, SidebarEntryKind.Album, al.Uri, al.Name ?? "", JoinArtists(al.Artists),
                    al.Cover, null, al.TrackCount, added,
                    SortStamp: added > 0 ? added : seen.Stamp(id),
                    LastVisitedTicksUtc: rec.LastVisitedTicks(id),
                    SourceOrder: i, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
                {
                    FolderId = "", FolderName = "",
                    FirstArtistName = al.Artists is { Count: > 0 } ? al.Artists[0].Name : "",
                });
            }

        if ((kinds & SidebarEntryKindMask.Artist) != 0)
            for (int i = 0; i < artists.Count; i++)
            {
                var ar = artists[i];
                string id = SidebarPinId.ArtistPrefix + ar.Uri;
                long added = Added(added0, ar.Uri);
                into.Add(new SidebarLibraryEntry(
                    id, SidebarEntryKind.Artist, ar.Uri, ar.Name ?? "", "",
                    ar.Image, null, 0, added,
                    SortStamp: added > 0 ? added : seen.Stamp(id),
                    LastVisitedTicksUtc: rec.LastVisitedTicks(id),
                    SourceOrder: i, Depth: 0, Circular: true, Flavor: SidebarPlaylistFlavor.None)
                { FolderId = "", FolderName = "", FirstArtistName = "" });
            }

        if ((kinds & SidebarEntryKindMask.Show) != 0)
            for (int i = 0; i < shows.Count; i++)
            {
                var sh = shows[i];
                string id = SidebarPinId.ShowPrefix + sh.Uri;
                long added = Added(added0, sh.Uri);
                into.Add(new SidebarLibraryEntry(
                    id, SidebarEntryKind.Show, sh.Uri, sh.Name ?? "", sh.Publisher ?? "",
                    sh.Cover, null, 0, added,
                    SortStamp: added > 0 ? added : seen.Stamp(id),
                    LastVisitedTicksUtc: rec.LastVisitedTicks(id),
                    SourceOrder: i, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
                { FolderId = "", FolderName = "", FirstArtistName = "" });
            }

        return new SidebarProjectionResult(into.Count, flavorMask, seen.NewStamps - stampsBefore);
    }

    // The tree walk. A folder is emitted as one opaque row (when its kind is wanted); its children follow only when the
    // caller asked for a flattened tree or the folder is expanded. When folders are NOT wanted the walk still descends —
    // a hidden folder must not hide its playlists (that is exactly the flat-consumer guarantee, one level up).
    static void Walk(IReadOnlyList<PlaylistNode> nodes, List<SidebarLibraryEntry> into,
                     string folderId, string folderName, int depth, ref int order, ref byte flavorMask,
                     bool wantPlaylists, bool wantFolders, bool includeFolderChildren,
                     Func<string, bool>? isFolderExpanded,
                     IReadOnlyDictionary<string, long> addedAt, SidebarRecency recency, SidebarFirstSeen firstSeen)
    {
        if (depth > SidebarTree.MaxDepth) return;
        for (int i = 0; i < nodes.Count; i++)
        {
            switch (nodes[i])
            {
                case PlaylistLeaf leaf:
                {
                    if (!wantPlaylists) break;
                    var p = leaf.Playlist;
                    string id = SidebarPinId.PlaylistPrefix + p.Uri;
                    var flavor = FlavorOf(p);
                    flavorMask |= (byte)(1 << (int)flavor);
                    // Playlists have NO add timestamp anywhere (the rootlist is an ordered marker stream, not a
                    // timestamped SavedItem set) → AddedAtMs stays 0 and the sort key is the local first-seen proxy.
                    long added = Added(addedAt, p.Uri);
                    into.Add(new SidebarLibraryEntry(
                        id, SidebarEntryKind.Playlist, p.Uri, p.Name ?? "", p.OwnerName ?? "",
                        p.Cover, p.Cover is null ? p.MosaicTiles : null, p.TrackCount, added,
                        SortStamp: added > 0 ? added : firstSeen.Stamp(id),
                        LastVisitedTicksUtc: recency.LastVisitedTicks(id),
                        SourceOrder: order++, Depth: depth, Circular: false, Flavor: flavor)
                    {
                        FolderId = folderId, FolderName = folderName,
                        ParentFolderId = folderId, ParentFolderName = folderName,
                        IsOwner = p.IsOwner, CanEdit = p.CanEdit, FirstArtistName = "",
                    });
                    break;
                }

                case PlaylistFolder f:
                {
                    if (wantFolders)
                    {
                        string id = SidebarPinId.ForFolder(f.Id);
                        into.Add(new SidebarLibraryEntry(
                            id, SidebarEntryKind.Folder, "", f.Name ?? "", "",
                            null, FolderTiles(f.Items), DirectChildCount(f.Items), 0,
                            SortStamp: 0,                                  // structural: never a member of a sorted band
                            LastVisitedTicksUtc: 0,                        // a folder never navigates, so it is never visited
                            SourceOrder: order++, Depth: depth, Circular: false, Flavor: SidebarPlaylistFlavor.None)
                        {
                            FolderId = f.Id, FolderName = f.Name ?? "",
                            // A folder's OWN id is FolderId; the containing folder is the walk's current level, which is
                            // the only thing "Move out of {folder}" can be built from.
                            ParentFolderId = folderId, ParentFolderName = folderName,
                            FirstArtistName = "",
                        });
                    }

                    bool descend = includeFolderChildren || !wantFolders || (isFolderExpanded?.Invoke(f.Id) ?? false);
                    if (descend)
                        Walk(f.Items, into, f.Id, f.Name ?? "", depth + 1, ref order, ref flavorMask,
                             wantPlaylists, wantFolders, includeFolderChildren, isFolderExpanded, addedAt, recency, firstSeen);
                    break;
                }
            }
        }
    }

    /// <summary>Playlist provenance, derived from the only three facts a <see cref="PlaylistSummary"/> carries (F.7.2).
    /// Deliberately conservative: <see cref="SidebarPlaylistFlavor.None"/> means "the data does not say", never "mine".</summary>
    public static SidebarPlaylistFlavor FlavorOf(in PlaylistSummary p) =>
          p.IsOwner ? SidebarPlaylistFlavor.ByYou
        : string.Equals(p.OwnerName, "Spotify", StringComparison.OrdinalIgnoreCase) ? SidebarPlaylistFlavor.BySpotify
        : p.CanEdit ? SidebarPlaylistFlavor.Mixed                    // collaborative
        : p.OwnerName is { Length: > 0 } ? SidebarPlaylistFlavor.Mixed   // someone else's
        : SidebarPlaylistFlavor.None;                                    // unknown

    /// <summary>Locked decision 10's "qualifier chips only when the data supports them", made mechanical: at least TWO
    /// distinct NON-unknown flavors must be present. A persisted qualifier other than Any is treated as Any at filter
    /// time whenever this is false, so a stale preference can never hide the whole list.</summary>
    public static bool QualifiersAvailable(byte flavorMask) => BitOperations.PopCount((uint)(flavorMask & 0b1110)) >= 2;

    /// <summary>
    /// Stable-partition so PINNED entries lead, in PIN ORDER (the pin store's order — NOT the sort order), followed by the
    /// remaining entries in sort order. Applies to EVERY sort mode including Custom. A pinned entry the current filter
    /// excluded simply is not here: pins surface only within the kinds the caller asked for.
    ///
    /// Returns the length of the leading pin band (<c>prefs.Entries.PinCount</c>). Pinned rows are stamped
    /// <see cref="SidebarLibraryEntry.IsPinned"/> in place, so a row never has to ask the pin store per frame.
    /// </summary>
    /// <param name="scratch">A reusable buffer owned by the caller (UseRef) — one pass, no OrderBy/Where.</param>
    public static int PinsFirst(List<SidebarLibraryEntry> list, IReadOnlyList<SidebarPin>? pins,
                                List<SidebarLibraryEntry> scratch)
    {
        if (pins is null || pins.Count == 0 || list.Count == 0) return 0;

        var rank = new Dictionary<string, int>(pins.Count, StringComparer.Ordinal);
        for (int i = 0; i < pins.Count; i++)
            if (pins[i].Id is { Length: > 0 } id) rank.TryAdd(id, i);

        var foundAt = new Dictionary<int, int>(pins.Count);          // pin index → index in list
        for (int i = 0; i < list.Count; i++)
            if (rank.TryGetValue(list[i].Id, out int pi)) foundAt.TryAdd(pi, i);
        if (foundAt.Count == 0) return 0;

        scratch.Clear();
        for (int pi = 0; pi < pins.Count; pi++)
            if (foundAt.TryGetValue(pi, out int li)) scratch.Add(list[li] with { IsPinned = true });
        int band = scratch.Count;
        for (int i = 0; i < list.Count; i++)
            if (!rank.ContainsKey(list[i].Id)) scratch.Add(list[i]);

        list.Clear();
        for (int i = 0; i < scratch.Count; i++) list.Add(scratch[i]);
        return band;
    }

    /// <summary>Allocating convenience overload (tests, cold paths).</summary>
    public static int PinsFirst(List<SidebarLibraryEntry> list, IReadOnlyList<SidebarPin>? pins)
        => PinsFirst(list, pins, new List<SidebarLibraryEntry>(list.Count));

    /// <summary>Append every projected id into <paramref name="into"/> — the "still present" set
    /// <see cref="SidebarFirstSeen.PruneTo"/> needs on save.</summary>
    public static void CollectIds(IReadOnlyList<SidebarLibraryEntry> list, List<string> into)
    {
        for (int i = 0; i < list.Count; i++) into.Add(list[i].Id);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────

    static long Added(IReadOnlyDictionary<string, long> addedAt, string? uri) =>
        uri is { Length: > 0 } && addedAt.Count > 0 && addedAt.TryGetValue(uri, out long ms) && ms > 0 ? ms : 0L;

    static int DirectChildCount(IReadOnlyList<PlaylistNode> items) => items.Count;

    // The first up-to-4 child covers of a folder, for the folder glyph's 2×2 mosaic. Folders are few, so the small list
    // per folder per rebuild is deliberate; null when the folder has no art at all (the glyph fallback renders).
    static IReadOnlyList<string>? FolderTiles(IReadOnlyList<PlaylistNode> items)
    {
        List<string>? tiles = null;
        for (int i = 0; i < items.Count && (tiles is null || tiles.Count < 4); i++)
        {
            if (items[i] is not PlaylistLeaf leaf) continue;
            var url = leaf.Playlist.Cover?.Url;
            if (string.IsNullOrEmpty(url)) continue;
            (tiles ??= new List<string>(4)).Add(url);
        }
        return tiles;
    }

    // "A, B, C" capped at three names, then "…". One artist (the overwhelming case) returns the source string with no
    // allocation at all.
    static string JoinArtists(IReadOnlyList<ArtistRef>? artists)
    {
        if (artists is null || artists.Count == 0) return "";
        if (artists.Count == 1) return artists[0].Name ?? "";
        s_join.Clear();
        int n = artists.Count < 3 ? artists.Count : 3;
        for (int i = 0; i < n; i++)
        {
            if (i > 0) s_join.Append(", ");
            s_join.Append(artists[i].Name);
        }
        if (artists.Count > n) s_join.Append('…');
        return s_join.ToString();
    }
}
