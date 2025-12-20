namespace Wavee.WinUI.Helpers;

/// <summary>
/// Helper class for working with Spotify URIs
/// </summary>
public static class SpotifyUriHelper
{
    /// <summary>
    /// Converts a Spotify URI to a web URL
    /// </summary>
    /// <param name="uri">Spotify URI (e.g., "spotify:album:123")</param>
    /// <returns>Web URL (e.g., "https://open.spotify.com/album/123")</returns>
    public static string? UriToWebUrl(string? uri)
    {
        if (string.IsNullOrEmpty(uri) || !uri.StartsWith("spotify:"))
            return uri;

        // Parse URI: spotify:type:id
        var parts = uri.Split(':');
        if (parts.Length < 3)
            return uri;

        var type = parts[1]; // album, playlist, artist, show, episode, track, etc.
        var id = parts[2];

        return $"https://open.spotify.com/{type}/{id}";
    }

    /// <summary>
    /// Extracts the ID from a Spotify URI
    /// </summary>
    /// <param name="uri">Spotify URI</param>
    /// <returns>The ID portion of the URI</returns>
    public static string? GetIdFromUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri) || !uri.StartsWith("spotify:"))
            return uri;

        var parts = uri.Split(':');
        return parts.Length >= 3 ? parts[2] : uri;
    }

    /// <summary>
    /// Extracts the type from a Spotify URI
    /// </summary>
    /// <param name="uri">Spotify URI</param>
    /// <returns>The type portion (album, playlist, artist, etc.)</returns>
    public static string? GetTypeFromUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri) || !uri.StartsWith("spotify:"))
            return null;

        var parts = uri.Split(':');
        return parts.Length >= 2 ? parts[1] : null;
    }
}
