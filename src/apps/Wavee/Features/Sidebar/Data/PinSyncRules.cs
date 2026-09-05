using System;
using Wavee.Core;

namespace Wavee;

/// <summary>The ONE rule for which sidebar pins mirror Spotify's <c>ylpin</c> set, and how a pin id and a wire uri map
/// onto each other. Engine-free (System + Wavee.Core), source-included by Wavee.Tests. Everything that is NOT a
/// Spotify playlist/album/artist/show or the Liked Songs route stays a local-only pin — folders, app routes,
/// <c>wavee:</c> playlists.
///
/// <para>The Liked Songs wire uri (<see cref="TryWireUri"/>'s <c>"liked"</c> arm) is the one open item this plan
/// carries (docs/plans/wavee/pin-spotify-sync-implementation.md §0.1/§5): the `/json` dealer push describes it as
/// <c>{"type":"collection"}</c> with no identifier, and no capture we hold shows the exact uri the `/paging` response
/// carries for it. <c>spotify:user:{username}:collection</c> is the best-supported guess — the one user-namespaced
/// spelling Spotify's own clients use elsewhere — pending a <c>--spotify-sync</c> capture confirming it
/// (<see cref="Wavee.SpotifyLive.SpotifyLibrarySync"/> logs the first few <c>"pins"</c> uris for exactly this).
/// TODO(pin-sync-liked-uri): if the capture disagrees, only this one arm changes.</para></summary>
public static class PinSyncRules
{
    /// <summary>pin id → the collection2v2 item uri, or null when this pin is local-only.</summary>
    public static string? TryWireUri(string? pinId, string username)
    {
        if (string.IsNullOrEmpty(pinId)) return null;
        if (string.Equals(pinId, "liked", StringComparison.Ordinal))
            return username.Length > 0 ? "spotify:user:" + username + ":collection" : null;
        switch (SidebarPinId.KindOf(pinId))
        {
            case SidebarEntryKind.Playlist:
            case SidebarEntryKind.Album:
            case SidebarEntryKind.Artist:
            case SidebarEntryKind.Show:
                string uri = SidebarPinId.UriOf(pinId);
                return uri.StartsWith("spotify:", StringComparison.Ordinal) ? uri : null;   // wavee:playlist:* stays local-only
            default:
                return null;   // folders, app routes
        }
    }

    /// <summary>wire uri → the canonical pin id, or null when the server sent something this client cannot pin (a
    /// track, an episode, an unknown scheme, a binary blob that happened to parse as a uri-shaped string).</summary>
    public static string? TryPinId(string? wireUri)
    {
        if (string.IsNullOrEmpty(wireUri) || !wireUri.StartsWith("spotify:", StringComparison.Ordinal)) return null;
        return SidebarPinId.FromUri(wireUri);   // collapses every Liked spelling onto "liked"; refuses tracks/episodes
    }

    /// <summary>Whether this pin id mirrors the server's ylpin set at all — the read-side counterpart of
    /// <see cref="TryPinId"/>, used to decide which LOCAL pins are eligible for a server-driven removal / write-back.</summary>
    public static bool IsSyncable(string? pinId, string username) => TryWireUri(pinId, username) is not null;
}
