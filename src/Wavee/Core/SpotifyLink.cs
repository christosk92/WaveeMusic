namespace Wavee.Core;

/// <summary>
/// Kinds of Spotify links the omnibar can resolve. Covers entity types that have a
/// dedicated app destination plus a few pseudo-URI forms (Liked Songs / Your Episodes /
/// Genre browse page).
/// </summary>
public enum SpotifyLinkKind
{
    Track,
    Album,
    Artist,
    Playlist,
    Show,
    Episode,
    User,
    LikedSongs,
    YourEpisodes,
    Genre,
    Unknown,
}

/// <summary>
/// A parsed Spotify URL (<c>https://open.spotify.com/...</c>) or URI
/// (<c>spotify:...</c>) reduced to a kind + canonical URI ready to hand to
/// navigation helpers / metadata fetchers.
/// </summary>
/// <remarks>
/// This is intentionally separate from <see cref="Audio.SpotifyId"/>: SpotifyId is a
/// strict 128-bit base62 identifier, but Liked Songs / Your Episodes / Genre /
/// User links don't fit that shape (no id at all, or free-form usernames). This type
/// is the omnibar-shaped wrapper that handles all of them uniformly.
/// </remarks>
public readonly record struct SpotifyLink(
    SpotifyLinkKind Kind,
    string CanonicalUri,
    string? EntityId)
{
    public static bool TryParse(string? text, out SpotifyLink result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseUrl(trimmed, out result);
        }
        if (trimmed.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseUri(trimmed, out result);
        }
        return false;
    }

    private static bool TryParseUrl(string url, out SpotifyLink result)
    {
        result = default;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (!string.Equals(uri.Host, "open.spotify.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;

        int idx = 0;
        // Drop locale prefix: intl-{lang}[-{region}], e.g. /intl-de/album/xxx.
        if (segments[idx].StartsWith("intl-", StringComparison.OrdinalIgnoreCase)
            && segments.Length > idx + 1)
        {
            idx++;
        }
        if (idx >= segments.Length) return false;

        var type = segments[idx].ToLowerInvariant();
        var rest = segments.Length > idx + 1 ? segments[idx + 1] : null;

        switch (type)
        {
            case "track":
                if (rest is null || !IsBase62Id(rest)) return false;
                result = new SpotifyLink(SpotifyLinkKind.Track, $"spotify:track:{rest}", rest);
                return true;
            case "album":
                if (rest is null || !IsBase62Id(rest)) return false;
                result = new SpotifyLink(SpotifyLinkKind.Album, $"spotify:album:{rest}", rest);
                return true;
            case "artist":
                if (rest is null || !IsBase62Id(rest)) return false;
                result = new SpotifyLink(SpotifyLinkKind.Artist, $"spotify:artist:{rest}", rest);
                return true;
            case "playlist":
                if (rest is null || !IsBase62Id(rest)) return false;
                result = new SpotifyLink(SpotifyLinkKind.Playlist, $"spotify:playlist:{rest}", rest);
                return true;
            case "show":
                if (rest is null || !IsBase62Id(rest)) return false;
                result = new SpotifyLink(SpotifyLinkKind.Show, $"spotify:show:{rest}", rest);
                return true;
            case "episode":
                if (rest is null || !IsBase62Id(rest)) return false;
                result = new SpotifyLink(SpotifyLinkKind.Episode, $"spotify:episode:{rest}", rest);
                return true;
            case "user":
                if (string.IsNullOrWhiteSpace(rest)) return false;
                var userId = Uri.UnescapeDataString(rest);
                result = new SpotifyLink(SpotifyLinkKind.User, $"spotify:user:{userId}", userId);
                return true;
            case "collection":
                if (string.Equals(rest, "tracks", StringComparison.OrdinalIgnoreCase))
                {
                    result = new SpotifyLink(SpotifyLinkKind.LikedSongs, "spotify:collection", null);
                    return true;
                }
                if (string.Equals(rest, "your-episodes", StringComparison.OrdinalIgnoreCase))
                {
                    result = new SpotifyLink(SpotifyLinkKind.YourEpisodes, "spotify:collection:your-episodes", null);
                    return true;
                }
                return false;
            case "genre":
                if (string.IsNullOrWhiteSpace(rest)) return false;
                result = new SpotifyLink(SpotifyLinkKind.Genre, $"spotify:page:{rest}", rest);
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseUri(string raw, out SpotifyLink result)
    {
        result = default;

        // Pseudo-URIs first — these don't fit the generic spotify:{type}:{id} shape.
        if (string.Equals(raw, "spotify:collection", StringComparison.OrdinalIgnoreCase))
        {
            result = new SpotifyLink(SpotifyLinkKind.LikedSongs, "spotify:collection", null);
            return true;
        }
        if (string.Equals(raw, "spotify:collection:your-episodes", StringComparison.OrdinalIgnoreCase))
        {
            result = new SpotifyLink(SpotifyLinkKind.YourEpisodes, "spotify:collection:your-episodes", null);
            return true;
        }
        // Legacy form: spotify:user:{username}:collection → Liked Songs.
        if (raw.StartsWith("spotify:user:", StringComparison.OrdinalIgnoreCase)
            && raw.EndsWith(":collection", StringComparison.OrdinalIgnoreCase))
        {
            result = new SpotifyLink(SpotifyLinkKind.LikedSongs, "spotify:collection", null);
            return true;
        }
        if (raw.StartsWith("spotify:page:", StringComparison.OrdinalIgnoreCase))
        {
            var idx = raw["spotify:page:".Length..];
            if (string.IsNullOrEmpty(idx)) return false;
            result = new SpotifyLink(SpotifyLinkKind.Genre, $"spotify:page:{idx}", idx);
            return true;
        }

        var parts = raw.Split(':');
        if (parts.Length < 3) return false;
        if (!string.Equals(parts[0], "spotify", StringComparison.OrdinalIgnoreCase)) return false;

        var type = parts[1].ToLowerInvariant();
        var id = parts[2];

        switch (type)
        {
            case "track":
                if (!IsBase62Id(id)) return false;
                result = new SpotifyLink(SpotifyLinkKind.Track, $"spotify:track:{id}", id);
                return true;
            case "album":
                if (!IsBase62Id(id)) return false;
                result = new SpotifyLink(SpotifyLinkKind.Album, $"spotify:album:{id}", id);
                return true;
            case "artist":
                if (!IsBase62Id(id)) return false;
                result = new SpotifyLink(SpotifyLinkKind.Artist, $"spotify:artist:{id}", id);
                return true;
            case "playlist":
                if (!IsBase62Id(id)) return false;
                result = new SpotifyLink(SpotifyLinkKind.Playlist, $"spotify:playlist:{id}", id);
                return true;
            case "show":
                if (!IsBase62Id(id)) return false;
                result = new SpotifyLink(SpotifyLinkKind.Show, $"spotify:show:{id}", id);
                return true;
            case "episode":
                if (!IsBase62Id(id)) return false;
                result = new SpotifyLink(SpotifyLinkKind.Episode, $"spotify:episode:{id}", id);
                return true;
            case "user":
                if (string.IsNullOrWhiteSpace(id)) return false;
                result = new SpotifyLink(SpotifyLinkKind.User, $"spotify:user:{id}", id);
                return true;
            default:
                return false;
        }
    }

    private static bool IsBase62Id(ReadOnlySpan<char> s)
    {
        if (s.Length != 22) return false;
        foreach (var c in s)
        {
            if (!((c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z')))
                return false;
        }
        return true;
    }
}
