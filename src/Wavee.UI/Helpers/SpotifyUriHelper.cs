using System;

namespace Wavee.UI.Helpers;

/// <summary>
/// Canonical Spotify-URI utility. Replaces every inline <c>.StartsWith("spotify:track:")</c>
/// and <c>$"spotify:{kind}:{id}"</c> across the codebase. Also the home for the
/// public-URL projection (<see cref="ToHttps"/>) previously duplicated in the
/// drag-drop subsystem.
/// </summary>
internal static class SpotifyUriHelper
{
    /// <summary>
    /// Returns true if the URI is the <c>spotify:{kind}:{id}</c> form for
    /// <paramref name="kind"/>. Null / empty / wrong-kind / malformed → false.
    /// Case-sensitive on the <c>spotify:</c> + kind prefix (Spotify URIs are
    /// always lowercase by spec; never seen otherwise on the wire).
    /// </summary>
    public static bool IsKind(string? uri, SpotifyEntityKind kind)
    {
        if (string.IsNullOrEmpty(uri) || kind == SpotifyEntityKind.Unknown) return false;
        var prefix = "spotify:" + KindToString(kind) + ":";
        // Liked Songs is special — it's "spotify:collection" with no third segment.
        if (kind == SpotifyEntityKind.Collection)
            return uri.StartsWith("spotify:collection", StringComparison.Ordinal);
        return uri.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Parses a <c>spotify:{kind}:{id}</c> URI into its parts. Returns false for
    /// null, empty, non-Spotify, or unrecognized-kind inputs.
    /// </summary>
    public static bool TryParse(string? uri, out SpotifyEntityKind kind, out string id)
    {
        kind = SpotifyEntityKind.Unknown;
        id = string.Empty;
        if (string.IsNullOrEmpty(uri)) return false;
        if (!uri.StartsWith("spotify:", StringComparison.Ordinal)) return false;

        var parts = uri.Split(':');
        if (parts.Length < 2) return false;

        kind = StringToKind(parts[1]);
        if (kind == SpotifyEntityKind.Unknown) return false;

        // "spotify:collection" has no id segment — valid Liked-Songs URI.
        if (kind == SpotifyEntityKind.Collection)
        {
            id = parts.Length >= 3 ? parts[2] : string.Empty;
            return true;
        }

        if (parts.Length < 3) return false;
        id = parts[2];
        return !string.IsNullOrEmpty(id);
    }

    /// <summary>
    /// Composes a <c>spotify:{kind}:{id}</c> URI. Throws on Unknown / empty id.
    /// </summary>
    public static string ToUri(SpotifyEntityKind kind, string id)
    {
        if (kind == SpotifyEntityKind.Unknown)
            throw new ArgumentException("Cannot build URI for Unknown kind", nameof(kind));
        if (kind == SpotifyEntityKind.Collection && string.IsNullOrEmpty(id))
            return "spotify:collection";
        ArgumentException.ThrowIfNullOrEmpty(id);
        return "spotify:" + KindToString(kind) + ":" + id;
    }

    /// <summary>
    /// Converts a <c>spotify:{kind}:{id}</c> URI to its public
    /// <c>https://open.spotify.com/{kind}/{id}</c> form. Returns null for inputs
    /// that don't represent a navigable entity (folders, malformed strings,
    /// null, empty).
    /// </summary>
    public static string? ToHttps(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        var parts = uri.Split(':');
        if (parts.Length < 3 || !string.Equals(parts[0], "spotify", StringComparison.Ordinal))
            return null;
        // Folders / private "wavee:" URIs / non-entity kinds: caller decides.
        return $"https://open.spotify.com/{parts[1]}/{parts[2]}";
    }

    /// <summary>
    /// Bare id from a <c>spotify:{kind}:{id}</c> form. Returns the input
    /// unchanged when it's already a bare id (no colon). Empty string for null /
    /// empty inputs.
    /// </summary>
    public static string BareId(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return string.Empty;
        var idx = uri.LastIndexOf(':');
        return idx < 0 ? uri : uri[(idx + 1)..];
    }

    private static string KindToString(SpotifyEntityKind kind) => kind switch
    {
        SpotifyEntityKind.Track      => "track",
        SpotifyEntityKind.Album      => "album",
        SpotifyEntityKind.Artist     => "artist",
        SpotifyEntityKind.Playlist   => "playlist",
        SpotifyEntityKind.Episode    => "episode",
        SpotifyEntityKind.Show       => "show",
        SpotifyEntityKind.User       => "user",
        SpotifyEntityKind.Collection => "collection",
        _ => throw new InvalidOperationException("Unknown kind"),
    };

    private static SpotifyEntityKind StringToKind(string s) => s switch
    {
        "track"      => SpotifyEntityKind.Track,
        "album"      => SpotifyEntityKind.Album,
        "artist"     => SpotifyEntityKind.Artist,
        "playlist"   => SpotifyEntityKind.Playlist,
        "episode"    => SpotifyEntityKind.Episode,
        "show"       => SpotifyEntityKind.Show,
        "user"       => SpotifyEntityKind.User,
        "collection" => SpotifyEntityKind.Collection,
        _            => SpotifyEntityKind.Unknown,
    };
}

internal enum SpotifyEntityKind
{
    Unknown = 0,
    Track,
    Album,
    Artist,
    Playlist,
    Episode,
    Show,
    User,
    /// <summary>The "Liked Songs" pseudo-entity (<c>spotify:collection</c>).</summary>
    Collection,
}
