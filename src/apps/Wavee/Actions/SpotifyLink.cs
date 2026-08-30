using System;
using System.Collections.Generic;
using System.Text;
using Wavee.Core;

namespace Wavee;

// spotify:{type}:{id}[:…] → https://open.spotify.com/{type}/{id}[/…]. Pure (Wavee.Core + BCL only) so the uri→url
// matrix + the multi-link join are unit-testable engine-free (Wavee.Tests source-includes this file). This is THE one
// converter — DetailPage.SpotifyPlaylistWebUrl and NotificationPanel.WebUrlFor delegate here (consolidation).
public static class SpotifyLink
{
    /// <summary><c>spotify:{type}:{id}</c> → <c>https://open.spotify.com/{type}/{id}</c>. Null for non-spotify uris
    /// (<c>wavee:local:*</c>, <c>wavee:playlist:*</c>, empty).</summary>
    // "Is this a Spotify uri?" is a PROVIDER question — asked through the ONE parser (hydration-facade-design.md §1.1)
    // rather than a scheme StartsWith. The slice that follows is the url TRANSFORM, not a second scheme test.
    public static string? WebUrl(string? uri) =>
        uri is { Length: > 0 } && EntityUri.Parse(uri).IsSpotify
            ? "https://open.spotify.com/" + uri["spotify:".Length..].Replace(':', '/')
            : null;

    /// <summary>The single raw <c>spotify:</c> uri a "Copy Spotify URI" / "Open in Spotify Web" acts on: a lone spotify
    /// track, or a spotify container uri. Null for multi-track sets and non-spotify targets (the single-target Share
    /// variants only apply to one shareable spotify entity).</summary>
    public static string? SingleUri(in ActionTarget target)
    {
        var tracks = target.Tracks;
        if (tracks is { Count: > 0 })
            return tracks.Count == 1 && EntityUri.Parse(tracks[0].Uri).IsSpotify ? tracks[0].Uri : null;
        return target.Uri is { Length: > 0 } u && EntityUri.Parse(u).IsSpotify ? u : null;
    }

    /// <summary>Does the target resolve to at least one shareable web link? (Tracks: any spotify track uri;
    /// containers: a spotify container uri.)</summary>
    public static bool HasLink(in ActionTarget target)
    {
        var tracks = target.Tracks;
        if (tracks is { Count: > 0 })
        {
            for (int i = 0; i < tracks.Count; i++)
                if (WebUrl(tracks[i].Uri) is not null) return true;
            return false;
        }
        return WebUrl(target.Uri) is not null;
    }

    /// <summary>The clipboard text for the target: each track's web url on its own line (multi → <c>\n</c>-joined,
    /// non-spotify rows skipped); a container target yields its single url. Null when nothing is linkable.</summary>
    public static string? LinkText(in ActionTarget target)
    {
        var tracks = target.Tracks;
        if (tracks is { Count: > 0 })
        {
            StringBuilder? sb = null;
            string? first = null;
            for (int i = 0; i < tracks.Count; i++)
            {
                if (WebUrl(tracks[i].Uri) is not { } url) continue;
                if (first is null) { first = url; continue; }
                sb ??= new StringBuilder(first);
                sb.Append('\n').Append(url);
            }
            return sb?.ToString() ?? first;
        }
        return WebUrl(target.Uri);
    }
}
