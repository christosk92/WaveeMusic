using System;
using Wavee.Core;

namespace Wavee;

/// <summary>The ONE rule for which sidebar pins mirror Spotify's <c>ylpin</c> set, and how a pin id and a wire uri map
/// onto each other. Engine-free (System + Wavee.Core), source-included by Wavee.Tests. Everything that is NOT a
/// Spotify playlist/album/artist/show or the Liked Songs route stays a local-only pin — folders, app routes,
/// <c>wavee:</c> playlists.
///
/// <para>§0.1/§5 — the Liked Songs wire uri is unconfirmed: the "liked" arm below ships the plan's best-supported
/// guess (<c>spotify:user:{username}:collection</c>, the user-namespaced spelling Spotify's own clients use) with a
/// TODO to correct it once a live <c>--spotify-sync</c> capture confirms the real one. The READ side already accepts
/// every spelling <see cref="EntityUri.IsLikedCollection"/> recognises, so a correction there needs no code change at
/// all — only this write-side guess might need updating.</para></summary>
public static class PinSyncRules
{
    /// <summary>pin id → the collection2v2 item uri, or null when this pin is local-only.</summary>
    public static string? TryWireUri(string? pinId, string username)
    {
        if (string.IsNullOrEmpty(pinId)) return null;
        if (string.Equals(pinId, "liked", StringComparison.Ordinal))
            // TODO(pin-sync-liked-uri): confirm against a live --spotify-sync capture (§5); until then this is the
            // best-supported guess, not a verified wire spelling.
            return username.Length > 0 ? "spotify:user:" + username + ":collection" : null;
        switch (SidebarPinId.KindOf(pinId))
        {
            case SidebarEntryKind.Playlist:
            case SidebarEntryKind.Album:
            case SidebarEntryKind.Artist:
            case SidebarEntryKind.Show:
                string uri = SidebarPinId.UriOf(pinId);
                return uri.StartsWith("spotify:", StringComparison.Ordinal) ? uri : null;
            default:
                return null;   // folders, app routes
        }
    }

    /// <summary>wire uri → the canonical pin id, or null when the server sent something this client cannot pin
    /// (a track, an episode, an unknown scheme, a binary blob).</summary>
    public static string? TryPinId(string? wireUri)
    {
        if (string.IsNullOrEmpty(wireUri) || !wireUri.StartsWith("spotify:", StringComparison.Ordinal)) return null;
        return SidebarPinId.FromUri(wireUri);   // collapses every Liked spelling onto "liked"; refuses tracks/episodes
    }

    public static bool IsSyncable(string? pinId, string username) => TryWireUri(pinId, username) is not null;
}
