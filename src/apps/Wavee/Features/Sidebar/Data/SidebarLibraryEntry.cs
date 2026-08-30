using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

// The UNIFIED sidebar row model (F.7.1) — one flat record every sidebar design renders, projected from LibraryStore's
// warm cells + HistoryStore recency by SidebarProjection.
//
// ENGINE-FREE BY CONSTRUCTION (System + Wavee.Core only), like SidebarDesign.cs / ShellResponsiveLayout.cs: this file and
// its Data/ siblings are source-included by src/apps/Wavee.Tests, so SidebarProjectionTests / SidebarSortTests drive the
// REAL projection instead of a copy of it. Nothing here may reference Signal<T>, Element, Icons, Loc or Tok.
//
// Locked decision 11: ICatalogSource.GetLibraryAsync is NOT the source (StoreLibrarySource returns empty for it). The
// projection reads LibraryStore.PlaylistTree / Albums / Artists / Shows / AddedAt instead.

/// <summary>The row families the unified list carries. This is also the ONE persisted pin-kind vocabulary
/// (<c>SidebarPin.Kind</c> — the deleted <c>SidebarPinKind</c> and its two lossy mappings are folded into this enum),
/// so a pin and a projected row can never disagree about what an entity "is". The unrelated
/// <c>Wavee.Core.Sidebar.SidebarEntityKind</c> is a different, narrower vocabulary (a curated section ITEM's target
/// kind) and is outside this unification.
///
/// <para><see cref="Track"/> is produced ONLY by a data source that yields tracks (<c>wavee.queue</c>,
/// <c>wavee.nowPlaying</c>, <c>wavee.artist.topTracks</c>) — never by <see cref="SidebarProjection.Build"/>, because a
/// track is not a library entity. It is deliberately absent from every <see cref="SidebarEntryKindMask"/> bit, so no
/// filter, qualifier or <c>EntityList</c> query can ever emit one, and <see cref="SidebarPinId.FromEntry"/> refuses it
/// (locked decision 4: tracks are not pinnable).</para></summary>
public enum SidebarEntryKind : byte
{
    AppRoute = 0, Playlist = 1, Folder = 2, Album = 3, Artist = 4, Show = 5, Track = 6,
}

/// <summary>Playlist provenance for the V3 qualifier chips. <see cref="None"/> = UNKNOWN (the data does not say) — the
/// chips stay hidden unless at least two DISTINCT non-None flavors are present, per locked decision 10. Byte values are
/// deliberately identical to <c>SidebarV3Qualifier</c> and <c>Wavee.Core.Sidebar.SidebarPlaylistQualifier</c>.</summary>
public enum SidebarPlaylistFlavor : byte { None = 0, ByYou = 1, BySpotify = 2, Mixed = 3 }

/// <summary>Which kinds a projection pass should emit. A mask (not a single kind) because every consumer asks for a
/// SET: the V3 "All" filter wants everything, a Curated <c>EntityList</c> section wants its query's kinds, and the
/// Podcasts chip wants shows only.</summary>
[Flags]
public enum SidebarEntryKindMask : byte
{
    None = 0,
    Playlist = 1,
    Folder = 2,
    Album = 4,
    Artist = 8,
    Show = 16,
    /// <summary>The playlist tree as the sidebar shows it: leaves AND their folders.</summary>
    PlaylistTree = Playlist | Folder,
    All = Playlist | Folder | Album | Artist | Show,
}

/// <summary>Mask helpers — the one place a filter/query vocabulary is translated into projection kinds, so no surface
/// hand-rolls the mapping (and no third mask type is invented).</summary>
public static class SidebarEntryKinds
{
    public static SidebarEntryKindMask Of(SidebarEntryKind kind) => kind switch
    {
        SidebarEntryKind.Playlist => SidebarEntryKindMask.Playlist,
        SidebarEntryKind.Folder => SidebarEntryKindMask.Folder,
        SidebarEntryKind.Album => SidebarEntryKindMask.Album,
        SidebarEntryKind.Artist => SidebarEntryKindMask.Artist,
        SidebarEntryKind.Show => SidebarEntryKindMask.Show,
        // AppRoute rows are authored, never projected (see ForRoute); Track rows come from a data source and are never a
        // member of a kind mask (see the SidebarEntryKind.Track remark) — both therefore match no filter.
        _ => SidebarEntryKindMask.None,
    };

    public static bool Has(SidebarEntryKindMask mask, SidebarEntryKind kind) => (mask & Of(kind)) != 0;

    /// <summary>The V3 chip row → kinds. Playlists includes folders (a folder IS part of the playlist tree); every other
    /// chip is a single kind. Locked decision 10's chip set, made mechanical.</summary>
    public static SidebarEntryKindMask From(SidebarV3Filter filter) => filter switch
    {
        SidebarV3Filter.Playlists => SidebarEntryKindMask.PlaylistTree,
        SidebarV3Filter.Podcasts => SidebarEntryKindMask.Show,
        SidebarV3Filter.Albums => SidebarEntryKindMask.Album,
        SidebarV3Filter.Artists => SidebarEntryKindMask.Artist,
        _ => SidebarEntryKindMask.All,
    };

    /// <summary>A Curated <c>SidebarEntityQuery.Kinds</c> → projection kinds. The Core mask has no folder bit, so a query
    /// that asks for playlists gets the tree (folders included) — that is what the section renders.</summary>
    public static SidebarEntryKindMask From(Wavee.Core.Sidebar.SidebarEntityKinds kinds)
    {
        var m = SidebarEntryKindMask.None;
        if ((kinds & Wavee.Core.Sidebar.SidebarEntityKinds.Playlists) != 0) m |= SidebarEntryKindMask.PlaylistTree;
        if ((kinds & Wavee.Core.Sidebar.SidebarEntityKinds.Albums) != 0) m |= SidebarEntryKindMask.Album;
        if ((kinds & Wavee.Core.Sidebar.SidebarEntityKinds.Artists) != 0) m |= SidebarEntryKindMask.Artist;
        if ((kinds & Wavee.Core.Sidebar.SidebarEntityKinds.Shows) != 0) m |= SidebarEntryKindMask.Show;
        return m;
    }
}

/// <summary>
/// One row of the unified sidebar list, projected from <c>LibraryStore</c> + <c>HistoryStore</c>. A readonly record
/// STRUCT: the projection fills a reusable <c>List&lt;T&gt;</c> owned by the mode component, so a rebuild allocates only
/// when the list grows — no per-frame LINQ, no per-row closures.
///
/// The positional members are the projection's own vocabulary (F.7.1). The trailing <c>init</c> members are the
/// display facts a surface needs but cannot derive (owner flags, the containing folder, an album's first artist) plus
/// <see cref="IsPinned"/>, which <see cref="SidebarProjection.PinsFirst"/> stamps. Everything else is computed, so the
/// §3.0 consumer names (<c>RouteKey</c>, <c>TrackCount</c>, <c>OwnerName</c>, <c>Publisher</c>, <c>FolderDepth</c>,
/// <c>IsFolder</c>, <c>PinKey</c>) resolve without a second parallel record.
/// </summary>
public readonly record struct SidebarLibraryEntry(
    string Id,                             // the pin/route id (F.5.4) — also the recency key and the custom-order key
    SidebarEntryKind Kind,
    string Uri,                            // "" for AppRoute and Folder
    string Name,
    string Creator,                        // playlist OwnerName · album joined artists · "" artist · show Publisher · "" folder/route
    Image? Cover,
    IReadOnlyList<string>? MosaicTiles,    // cover-less playlists (2×2 mosaic) + a folder's first child covers; else null
    int ChildCount,                        // playlist/album TrackCount · folder DIRECT child count · 0 for artist/show/route
    long AddedAtMs,                        // 0 = unknown (see SortStamp)
    long SortStamp,                        // the resolved "recently added" key (F.7.5) — never 0 for a sortable kind
    long LastVisitedTicksUtc,              // 0 = never visited
    int SourceOrder,                       // rootlist position for playlists/folders; else the source list index
    int Depth,                             // folder nesting depth (0 = top level)
    bool Circular,                         // artist avatars
    SidebarPlaylistFlavor Flavor)
{
    /// <summary>True when this row is in the pin store — stamped by <see cref="SidebarProjection.PinsFirst"/>, never
    /// guessed by a surface (the pin store is the only authority).</summary>
    public bool IsPinned { get; init; }

    // Field-backed so a default(SidebarLibraryEntry) (a scratch-list slot) still reads "" rather than null — these are
    // display strings a row concatenates without a null check.
    readonly string? _folderId;
    readonly string? _folderName;
    readonly string? _parentFolderId;
    readonly string? _parentFolderName;
    readonly string? _firstArtistName;

    /// <summary>The rootlist group id of the folder CONTAINING this row ("" at top level). For a folder row itself this
    /// is its OWN id, so a row never has to strip the <c>"folder:"</c> prefix off <see cref="Id"/>.</summary>
    public string FolderId { get => _folderId ?? ""; init => _folderId = value; }

    /// <summary>Display name of the folder containing this row ("" at top level; a folder row carries its own name).</summary>
    public string FolderName { get => _folderName ?? ""; init => _folderName = value; }

    /// <summary>The group id of the folder this row SITS IN ("" at top level) — for a folder row that is its PARENT,
    /// which <see cref="FolderId"/> cannot express (a folder's <see cref="FolderId"/> is its own id). It is what the
    /// "Move out of {folder}" verb needs, and the only reason a row can tell nested from top-level at all.</summary>
    public string ParentFolderId { get => _parentFolderId ?? ""; init => _parentFolderId = value; }

    /// <summary>Display name of <see cref="ParentFolderId"/> ("" at top level) — the {folder} in "Move out of".</summary>
    public string ParentFolderName { get => _parentFolderName ?? ""; init => _parentFolderName = value; }

    /// <summary>Playlist ownership (from <c>PlaylistSummary.IsOwner</c>) — gates the owner-only menu block.</summary>
    public bool IsOwner { get; init; }

    /// <summary>Whether the playlist is editable by the current user (<c>PlaylistSummary.CanEdit</c>).</summary>
    public bool CanEdit { get; init; }

    /// <summary>An album's FIRST billed artist, uncollapsed (<see cref="Creator"/> is the joined display string). "" for
    /// every other kind. Kept as a reference to the source string — no substring allocation.</summary>
    public string FirstArtistName { get => _firstArtistName ?? ""; init => _firstArtistName = value; }

    // ── computed §3.0 aliases (no storage, no second record) ──────────────────────────────────────────────────────────
    public bool IsPlayable => Kind is SidebarEntryKind.Playlist or SidebarEntryKind.Album or SidebarEntryKind.Show
                                   or SidebarEntryKind.Track;
    public bool IsFolder => Kind == SidebarEntryKind.Folder;

    /// <summary>True for a row that PLAYS on activation instead of navigating — a track has no detail route.</summary>
    public bool IsTrack => Kind == SidebarEntryKind.Track;

    /// <summary>The nav route this row opens — <see cref="Id"/> for every navigable kind, null for a folder (a folder
    /// expands in place; it never navigates) and for a track (it plays). Identical rule to
    /// <see cref="SidebarPinId.RouteOf"/>.</summary>
    public string? RouteKey => IsFolder || IsTrack ? null : Id;

    /// <summary>The pin-store key for this row. The id IS the pin id (F.5.4) for every pinnable kind.</summary>
    public string PinKey => Id;

    public int TrackCount => Kind is SidebarEntryKind.Playlist or SidebarEntryKind.Album ? ChildCount : 0;
    public string OwnerName => Kind == SidebarEntryKind.Playlist ? Creator : "";
    public string Publisher => Kind == SidebarEntryKind.Show ? Creator : "";
    public int FolderDepth => Depth;

    /// <summary>Qualifier-chip match. A qualifier of 0 (<c>Any</c>/<c>Unknown</c>) matches everything; the non-zero
    /// values of <c>SidebarV3Qualifier</c> and <c>Wavee.Core.Sidebar.SidebarPlaylistQualifier</c> are byte-identical to
    /// <see cref="SidebarPlaylistFlavor"/>, so one byte comparison serves both vocabularies.</summary>
    public bool MatchesQualifier(byte qualifier) => qualifier == 0 || (byte)Flavor == qualifier;

    public bool MatchesQualifier(SidebarV3Qualifier qualifier) => MatchesQualifier((byte)qualifier);

    /// <summary>An APP-ROUTE row (Home / Search / Liked / …). Authored by the surface, never produced by
    /// <see cref="SidebarProjection.Build"/>: the label + glyph come from <c>ShellNav.Dest</c>, which is engine-bound, so
    /// resolving them here would drag Icons/Loc into this layer. The route key IS the id (F.5.4).</summary>
    public static SidebarLibraryEntry ForRoute(string routeKey, string name, int sourceOrder = 0, long lastVisitedTicksUtc = 0) =>
        new(routeKey, SidebarEntryKind.AppRoute, "", name, "", null, null,
            ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: lastVisitedTicksUtc,
            SourceOrder: sourceOrder, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
        { FolderId = "", FolderName = "", FirstArtistName = "" };
}
