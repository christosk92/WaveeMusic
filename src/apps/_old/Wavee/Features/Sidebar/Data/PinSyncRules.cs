using System;
using Wavee.Core;

namespace Wavee;

/// <summary>The ONE rule for which sidebar pins mirror Spotify's <c>ylpin</c> set, and how a pin id and a wire uri map
/// onto each other. Engine-free (System + Wavee.Core), source-included by Wavee.Tests. Everything that is NOT a
/// Spotify playlist/album/artist/show, a rootlist folder, or the Liked Songs route stays a local-only pin — app
/// routes, <c>wavee:</c> playlists.
///
/// <para>Liked Songs is <c>spotify:collection</c> on the wire (captured 2026-09-11 with
/// <c>--spotify-collection pins</c>). The READ side already accepts every spelling
/// <see cref="EntityUri.IsLikedCollection"/> recognises, so this is the one write-side spelling that matters.</para></summary>
public static class PinSyncRules
{
    /// <summary>The wire uri Liked Songs pins as, inside the "pins" (ylpin) set — bare <c>spotify:collection</c>, NOT
    /// the <c>:tracks</c>-suffixed or user-namespaced forms the catalog/routing layer uses elsewhere.</summary>
    public const string LikedWireUri = "spotify:collection";

    /// <summary>pin id → the collection2v2 item uri, or null when this pin is local-only.</summary>
    public static string? TryWireUri(string? pinId, string username)
    {
        if (string.IsNullOrEmpty(pinId)) return null;
        if (string.Equals(pinId, "liked", StringComparison.Ordinal)) return LikedWireUri;
        switch (SidebarPinId.KindOf(pinId))
        {
            case SidebarEntryKind.Playlist:
            case SidebarEntryKind.Album:
            case SidebarEntryKind.Artist:
            case SidebarEntryKind.Show:
                string uri = SidebarPinId.UriOf(pinId);
                return uri.StartsWith("spotify:", StringComparison.Ordinal) ? uri : null;
            case SidebarEntryKind.Folder:
                string folderId = SidebarPinId.FolderIdOf(pinId);
                return folderId.Length > 0 ? EntityUri.FolderPrefix + folderId : null;
            default:
                return null;   // app routes only
        }
    }

    /// <summary>wire uri → the canonical pin id, or null when the server sent something this client cannot pin
    /// (a track, an episode, an unknown scheme, a binary blob).</summary>
    public static string? TryPinId(string? wireUri)
    {
        if (string.IsNullOrEmpty(wireUri) || !wireUri.StartsWith("spotify:", StringComparison.Ordinal)) return null;
        if (EntityUri.FolderIdOf(wireUri) is { Length: > 0 } folderId) return SidebarPinId.ForFolder(folderId);
        return SidebarPinId.FromUri(wireUri);   // collapses every Liked spelling onto "liked"; refuses tracks/episodes
    }

    public static bool IsSyncable(string? pinId, string username) => TryWireUri(pinId, username) is not null;
}
