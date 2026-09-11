using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

// S2 #7 (the sidebar half): opening a playlist/album from the SIDEBAR used to paint a header-less detail skeleton (no
// title, no cover, no reserved daylist countdown line) that reshaped once the full model landed, while the identical
// destination opened from a Home card did not — HomeCardNav.Open stashes the card's own PlaylistSummary/Album into
// NavPreviewStore (via DetailNav.OpenAlbum/OpenPlaylist) before navigating, and DetailPage paints its header from
// that stash on frame one. This file is the sidebar's half of the same decision: "which library record, if any,
// backs this row's navigation" — the STASH itself (NavPreviewStore.Set) and the actual navigation stay with the
// caller (SidebarPane.Navigate), exactly like DetailNav's own split.
//
// PURE (System + Wavee.Core only, like SidebarPinId) so the mapping is unit-tested directly against
// SidebarLibraryEntry / PlaylistSummary / Album rather than through a mounted SidebarPane.
public static class SidebarNavPreview
{
    /// <summary>Linear scan of the store's already-loaded flat playlist list for <paramref name="uri"/> — the exact
    /// technique <c>WaveeRootlist.IsMember</c> already uses over the same list, run once per click rather than per
    /// frame. Null when the store has not resolved it: an unlisted/editorial pin (e.g. a daylist never saved to the
    /// library — the sidebar's own projection never puts one of these in <c>LibraryStore.Playlists</c> either, see
    /// <c>SidebarProjectionBinder.ResolvePins</c>) or simply not loaded yet. An honest "no match", never a fabricated
    /// one.</summary>
    public static PlaylistSummary? FindPlaylist(IReadOnlyList<PlaylistSummary>? playlists, string uri)
    {
        if (playlists is null || uri.Length == 0) return null;
        for (int i = 0; i < playlists.Count; i++)
            if (string.Equals(playlists[i].Uri, uri, StringComparison.Ordinal)) return playlists[i];
        return null;
    }

    /// <summary>Same scan over the store's saved albums.</summary>
    public static Album? FindAlbum(IReadOnlyList<Album>? albums, string uri)
    {
        if (albums is null || uri.Length == 0) return null;
        for (int i = 0; i < albums.Count; i++)
            if (string.Equals(albums[i].Uri, uri, StringComparison.Ordinal)) return albums[i];
        return null;
    }

    /// <summary>The row's own display cache, shaped as the <see cref="PlaylistSummary"/> <c>DetailPreview.FromPlaylist</c>
    /// expects — the fallback for a uri <see cref="FindPlaylist"/> could not resolve (an unlisted pin). No daylist
    /// rollover window and no payload accent: <see cref="SidebarLibraryEntry"/> carries neither (they ride only the
    /// catalog's full playlist4 read), so the header still paints with a real title/cover/owner/track-count and simply
    /// skips the countdown reservation — exactly what an unresolved Home card would do too.</summary>
    public static PlaylistSummary PlaylistSummaryOf(in SidebarLibraryEntry e) =>
        new(e.Uri, e.Name, e.Creator, e.ChildCount, e.Cover, e.MosaicTiles, e.CanEdit, e.IsOwner);

    /// <summary>The row's own display cache as an <see cref="Album"/>. <c>Year</c> is unknown at row granularity (the
    /// projection never carries it) — 0, the same "unset" value <see cref="Album"/> already uses for it elsewhere.
    /// <c>Id</c> is the bare entity id (<see cref="EntityUri.IdOf"/>), matching <c>HomeCardNav.Id</c> — never the
    /// row's pin id (<see cref="SidebarLibraryEntry.Id"/> is <c>"album:" + uri</c>), which is a different identity.</summary>
    public static Album AlbumOf(in SidebarLibraryEntry e) =>
        new(EntityUri.IdOf(e.Uri), e.Uri, e.Name, e.Cover, Array.Empty<ArtistRef>(), 0, e.ChildCount);
}
