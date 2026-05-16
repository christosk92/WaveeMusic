using System;

namespace Wavee.UI.Services.DragDrop;

/// <summary>
/// Tiny helpers to project <c>spotify:{kind}:{id}</c> URIs to their public
/// <c>https://open.spotify.com/{kind}/{id}</c> form and back to the bare id.
/// Lives next to the payloads since drag-out is the primary consumer.
/// </summary>
internal static class SpotifyUri
{
    /// <summary>
    /// Converts <c>spotify:track:abc</c> → <c>https://open.spotify.com/track/abc</c>.
    /// Returns the input unchanged if it doesn't have at least three
    /// colon-separated segments (e.g. <c>spotify:start-group:xxx:name</c>
    /// becomes <c>https://open.spotify.com/start-group/xxx</c> — still
    /// stripped to two segments, which is fine for folders since they're
    /// filtered out by <c>SidebarReorderPayload.HttpsUrls</c> upstream).
    /// </summary>
    public static string ToHttps(string spotifyUri)
    {
        if (string.IsNullOrEmpty(spotifyUri))
            return string.Empty;

        var parts = spotifyUri.Split(':');
        if (parts.Length < 3 || !string.Equals(parts[0], "spotify", StringComparison.Ordinal))
            return spotifyUri;

        return $"https://open.spotify.com/{parts[1]}/{parts[2]}";
    }

    /// <summary>
    /// Bare id from a <c>spotify:track:abc</c> form (returns <c>abc</c>).
    /// </summary>
    public static string BareId(string spotifyUri)
    {
        if (string.IsNullOrEmpty(spotifyUri))
            return string.Empty;
        var idx = spotifyUri.LastIndexOf(':');
        return idx < 0 ? spotifyUri : spotifyUri[(idx + 1)..];
    }
}
