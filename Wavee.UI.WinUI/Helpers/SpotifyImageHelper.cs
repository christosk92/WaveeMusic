namespace Wavee.UI.WinUI.Helpers;

/// <summary>
/// Converts Spotify internal image URIs to loadable HTTPS URLs.
/// </summary>
internal static class SpotifyImageHelper
{
    /// <summary>
    /// Converts a Spotify image URI to an HTTPS URL that can be loaded by an Image control.
    /// </summary>
    /// <remarks>
    /// Handles these formats:
    /// - "https://i.scdn.co/image/..." → returned as-is (already HTTPS)
    /// - "spotify:image:ab67616d..." → "https://i.scdn.co/image/ab67616d..."
    /// - "spotify:mosaic:id1:id2:id3:id4" → null (composite images not supported yet)
    /// - null/empty → null
    /// </remarks>
    public static string? ToHttpsUrl(string? spotifyUri)
    {
        if (string.IsNullOrEmpty(spotifyUri))
            return null;

        // Already an HTTPS URL
        if (spotifyUri.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase))
            return spotifyUri;

        // spotify:image:hexid → https://i.scdn.co/image/hexid
        if (spotifyUri.StartsWith("spotify:image:", System.StringComparison.Ordinal))
        {
            var imageId = spotifyUri["spotify:image:".Length..];
            return $"https://i.scdn.co/image/{imageId}";
        }

        // spotify:mosaic: → not directly loadable (composite of 4 images)
        // Could be implemented later by fetching individual images and compositing
        if (spotifyUri.StartsWith("spotify:mosaic:", System.StringComparison.Ordinal))
            return null;

        return null;
    }
}
