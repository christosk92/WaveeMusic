using System;
using System.Collections.Generic;
using System.Linq;

namespace Wavee.UI.WinUI.Helpers;

/// <summary>
/// Converts Spotify internal image URIs to loadable HTTPS URLs.
/// </summary>
internal static class SpotifyImageHelper
{
    private const string MosaicPrefix = "spotify:mosaic:";
    private const string LocalArtworkPrefix = "wavee-artwork://";

    /// <summary>
    /// Root directory for the local-artwork cache. Set once at startup by the
    /// app composition root (AppRoot). Resolves <c>wavee-artwork://{hash}</c>
    /// URIs to <c>file:///</c> URLs the image pipeline can load.
    /// </summary>
    public static string? LocalArtworkRoot { get; set; }

    /// <summary>
    /// Converts a Spotify image URI (or wavee-artwork URI) to a URL that can be loaded by an Image control.
    /// </summary>
    /// <remarks>
    /// Handles these formats:
    /// - "https://i.scdn.co/image/..." → returned as-is (already HTTPS)
    /// - "spotify:image:ab67616d..." → "https://i.scdn.co/image/ab67616d..."
    /// - "spotify:mosaic:id1:id2:id3:id4" → null (single-image only — for the 2×2 composition
    ///   path see <see cref="TryParseMosaicTileUrls"/> + PlaylistMosaicService).
    /// - "wavee-artwork://{40-hex}" → "file:///{LocalArtworkRoot}/{hh}/{hash}.{ext}"
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

        if (spotifyUri.StartsWith(LocalArtworkPrefix, StringComparison.Ordinal))
        {
            return ResolveLocalArtwork(spotifyUri[LocalArtworkPrefix.Length..]);
        }

        return null;
    }

    private static string? ResolveLocalArtwork(string hash)
    {
        var root = LocalArtworkRoot;
        if (string.IsNullOrEmpty(root) || hash.Length < 2) return null;
        var dir = System.IO.Path.Combine(root, hash.Substring(0, 2));
        if (!System.IO.Directory.Exists(dir)) return null;

        // We don't know the extension at the URI level — pick the first file
        // whose stem matches. Cache writers always use lowercase hex stems.
        foreach (var ext in new[] { ".jpg", ".png", ".webp", ".gif", ".bmp" })
        {
            var candidate = System.IO.Path.Combine(dir, hash + ext);
            if (System.IO.File.Exists(candidate))
                return new Uri(candidate).AbsoluteUri;
        }

        // Fallback: any file starting with the hash.
        try
        {
            var match = System.IO.Directory.EnumerateFiles(dir, hash + "*")
                .FirstOrDefault();
            return match is null ? null : new Uri(match).AbsoluteUri;
        }
        catch
        {
            return null;
        }
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
