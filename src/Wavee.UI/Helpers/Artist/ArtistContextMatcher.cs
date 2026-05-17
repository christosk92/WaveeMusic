using System;

namespace Wavee.UI.Helpers.Artist;

/// <summary>
/// Tests whether a Spotify Connect "current context" URI represents the artist
/// page that's currently visible. Both forms accepted: a raw artist id
/// ("4tZwfgrHOc3mvqYlEYSvVi") or a fully-qualified URI ("spotify:artist:...").
/// </summary>
internal static class ArtistContextMatcher
{
    public static bool IsActive(string? currentContextUri, string? artistId)
    {
        if (string.IsNullOrWhiteSpace(currentContextUri) || string.IsNullOrWhiteSpace(artistId))
            return false;

        var canonicalArtistUri = artistId!.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase)
            ? artistId
            : "spotify:artist:" + artistId;

        return string.Equals(currentContextUri, artistId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(currentContextUri, canonicalArtistUri, StringComparison.OrdinalIgnoreCase);
    }
}
