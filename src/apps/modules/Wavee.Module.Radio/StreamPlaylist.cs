namespace Wavee.Module.Radio;

/// <summary>
/// Pure <c>.pls</c> / <c>.m3u</c> playlist parsing, kept separate from the module so it is unit-testable without
/// HTTP. HLS playlists are deliberately NOT unwrapped here: an <c>.m3u8</c> carrying <c>#EXT-X-</c> tags is a media
/// manifest that Media Foundation reads itself.
/// </summary>
public static class StreamPlaylist
{
    /// <summary>What a fetched body turned out to be.</summary>
    public enum Kind
    {
        /// <summary>Not a playlist: the url itself is the stream.</summary>
        Stream = 0,

        /// <summary>A SHOUTcast <c>.pls</c> INI playlist.</summary>
        Pls,

        /// <summary>A plain <c>.m3u</c> url list.</summary>
        M3u,

        /// <summary>An HLS manifest: hand the original url to the player untouched.</summary>
        Hls,
    }

    /// <summary>Classifies a fetched body.</summary>
    /// <param name="body">The body text.</param>
    /// <param name="contentType">The <c>Content-Type</c> header, when the server sent one.</param>
    public static Kind Classify(string? body, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(body)) return Kind.Stream;
        string text = body.TrimStart();

        if (text.Contains("#EXT-X-", StringComparison.Ordinal)) return Kind.Hls;
        if (text.StartsWith("[playlist]", StringComparison.OrdinalIgnoreCase)) return Kind.Pls;
        if (text.StartsWith("#EXTM3U", StringComparison.Ordinal)) return Kind.M3u;

        if (contentType is not null)
        {
            string ct = contentType.ToLowerInvariant();
            if (ct.Contains("scpls", StringComparison.Ordinal)) return Kind.Pls;
            if (ct.Contains("mpegurl", StringComparison.Ordinal)) return Kind.M3u;
        }

        // A body of bare urls (what most .m3u station files are: no #EXTM3U header at all).
        return HasUrlLine(text) ? Kind.M3u : Kind.Stream;
    }

    /// <summary>Reads the first stream url out of a playlist body.</summary>
    /// <param name="body">The playlist text.</param>
    /// <param name="kind">What <see cref="Classify"/> decided it is.</param>
    /// <param name="baseUrl">The url the body came from, so relative entries resolve.</param>
    /// <returns>The first stream url, or null when the playlist has none.</returns>
    public static string? FirstEntry(string? body, Kind kind, string? baseUrl = null)
        => kind switch
        {
            Kind.Pls => FirstPlsEntry(body, baseUrl),
            Kind.M3u => FirstM3uEntry(body, baseUrl),
            _ => null,
        };

    /// <summary>Reads <c>File1=…</c> (lowest index wins) out of a <c>.pls</c> body.</summary>
    /// <param name="body">The playlist text.</param>
    /// <param name="baseUrl">The url the body came from.</param>
    public static string? FirstPlsEntry(string? body, string? baseUrl = null)
    {
        if (string.IsNullOrEmpty(body)) return null;

        int bestIndex = int.MaxValue;
        string? best = null;

        foreach (string raw in Lines(body))
        {
            string line = raw.Trim();
            if (line.Length < 6 || !line.StartsWith("File", StringComparison.OrdinalIgnoreCase)) continue;

            int eq = line.IndexOf('=');
            if (eq < 0) continue;

            string digits = line[4..eq].Trim();
            if (!int.TryParse(digits, out int index)) continue;

            string value = line[(eq + 1)..].Trim();
            if (value.Length == 0) continue;

            if (index < bestIndex)
            {
                bestIndex = index;
                best = value;
            }
        }

        return Absolute(best, baseUrl);
    }

    /// <summary>Reads the first non-comment url out of an <c>.m3u</c> body.</summary>
    /// <param name="body">The playlist text.</param>
    /// <param name="baseUrl">The url the body came from.</param>
    public static string? FirstM3uEntry(string? body, string? baseUrl = null)
    {
        if (string.IsNullOrEmpty(body)) return null;

        foreach (string raw in Lines(body))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '#') continue;
            return Absolute(line, baseUrl);
        }

        return null;
    }

    private static bool HasUrlLine(string text)
    {
        foreach (string raw in Lines(text))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '#') continue;
            if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? Absolute(string? value, string? baseUrl)
    {
        if (value is null) return null;
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? absolute)) return absolute.ToString();
        if (baseUrl is not null && Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? root) &&
            Uri.TryCreate(root, value, out Uri? combined))
        {
            return combined.ToString();
        }

        return null;
    }

    private static IEnumerable<string> Lines(string text)
    {
        int start = 0;
        while (start <= text.Length)
        {
            int end = text.IndexOfAny(['\r', '\n'], start);
            if (end < 0)
            {
                if (start < text.Length) yield return text[start..];
                yield break;
            }

            if (end > start) yield return text[start..end];
            start = end + 1;
        }
    }
}
