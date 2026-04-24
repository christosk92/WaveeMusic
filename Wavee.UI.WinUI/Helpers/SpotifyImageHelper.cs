using System;
using System.Collections.Generic;

namespace Wavee.UI.WinUI.Helpers;

/// <summary>
/// Converts Spotify internal image URIs to loadable HTTPS URLs.
/// </summary>
internal static class SpotifyImageHelper
{
    private const string MosaicPrefix = "spotify:mosaic:";

    /// <summary>
    /// Converts a Spotify image URI to an HTTPS URL that can be loaded by an Image control.
    /// </summary>
    /// <remarks>
    /// Handles these formats:
    /// - "https://i.scdn.co/image/..." → returned as-is (already HTTPS)
    /// - "spotify:image:ab67616d..." → "https://i.scdn.co/image/ab67616d..."
    /// - "spotify:mosaic:id1:id2:id3:id4" → null (single-image only — for the 2×2 composition
    ///   path see <see cref="TryParseMosaicTileUrls"/> + PlaylistMosaicService).
    /// - null/empty → null
    /// </remarks>
    public static string? ToHttpsUrl(string? spotifyUri)
    {
        if (string.IsNullOrEmpty(spotifyUri))
            return null;

        if (spotifyUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return spotifyUri;

        if (spotifyUri.StartsWith("spotify:image:", StringComparison.Ordinal))
        {
            var imageId = spotifyUri["spotify:image:".Length..];
            return $"https://i.scdn.co/image/{imageId}";
        }

        return null;
    }

    /// <summary>
    /// Returns true if the URI is a Spotify mosaic descriptor (e.g. "spotify:mosaic:id1:id2:id3:id4").
    /// </summary>
    public static bool IsMosaicUri(string? uri)
        => !string.IsNullOrEmpty(uri) && uri.StartsWith(MosaicPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Parses "spotify:mosaic:id1:id2:id3:id4" into the 4 individual CDN URLs that compose the
    /// mosaic. Each id is a regular i.scdn.co image hash (same format as <c>spotify:image:</c>),
    /// so the tiles can be loaded via the standard image pipeline. Returns false when the URI
    /// is not a mosaic, has no ids, or contains only empty segments.
    /// </summary>
    public static bool TryParseMosaicTileUrls(string? mosaicUri, out IReadOnlyList<string> tileUrls)
    {
        if (!IsMosaicUri(mosaicUri))
        {
            tileUrls = Array.Empty<string>();
            return false;
        }

        var ids = mosaicUri![MosaicPrefix.Length..].Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (ids.Length == 0)
        {
            tileUrls = Array.Empty<string>();
            return false;
        }

        // Dedup: Spotify sometimes emits the same image id in multiple mosaic
        // positions for sparse playlists (<4 unique album covers). Feeding the
        // duplicates into the mosaic composition would render the same cover
        // in multiple quadrants; the caller can then decide whether to draw a
        // 2×2 or fall back to a single cover based on the distinct count.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var urls = new List<string>(ids.Length);
        foreach (var id in ids)
        {
            if (seen.Add(id))
                urls.Add($"https://i.scdn.co/image/{id}");
        }

        tileUrls = urls;
        return true;
    }
}
