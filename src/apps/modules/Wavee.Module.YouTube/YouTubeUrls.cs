using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Wavee.Module.YouTube;

/// <summary>What a pasted YouTube link turned out to be.</summary>
public enum YouTubeLinkKind
{
    /// <summary>Not a YouTube link at all.</summary>
    None = 0,

    /// <summary>The link already names a video id.</summary>
    Video,

    /// <summary>The link names a channel's "live" page; the id has to be scraped from the HTML.</summary>
    ChannelLive,
}

/// <summary>The result of parsing a pasted link.</summary>
/// <param name="Kind">Video, channel-live page, or nothing.</param>
/// <param name="VideoId">The 11-char video id when <paramref name="Kind"/> is <see cref="YouTubeLinkKind.Video"/>.</param>
/// <param name="LivePageUrl">The channel-live page to fetch when <paramref name="Kind"/> is <see cref="YouTubeLinkKind.ChannelLive"/>.</param>
public readonly record struct YouTubeLink(YouTubeLinkKind Kind, string? VideoId, string? LivePageUrl);

/// <summary>
/// Pure URL → video-id parsing, kept separate from the module so the whole matching table is unit-testable without
/// any HTTP. Mirrors yt-dlp's accepted forms (snapshot 2026-08-22).
/// </summary>
public static partial class YouTubeUrls
{
    private static readonly string[] Hosts =
    [
        "youtube.com", "youtu.be", "youtube-nocookie.com", "youtubekids.com", "youtube.googleapis.com",
    ];

    /// <summary>Path segments that look like a video id but never are.</summary>
    private static readonly string[] NotIds = ["videoseries", "live_stream"];

    [GeneratedRegex("^[0-9A-Za-z_-]{11}$")]
    private static partial Regex IdShape();

    [GeneratedRegex(@"^/(?:v|e|embed|shorts|live)/(?<id>[0-9A-Za-z_-]{11})(?:/|$)")]
    private static partial Regex PathId();

    [GeneratedRegex(@"^/(?:(?:c|channel|user)/)?@?(?<name>[^/]+)/live/?$")]
    private static partial Regex ChannelLivePath();

    /// <summary>True when <paramref name="text"/> is exactly an 11-character video id.</summary>
    /// <param name="text">Candidate text.</param>
    public static bool IsVideoId(string? text) => text is not null && IdShape().IsMatch(text);

    /// <summary>Parses a pasted link (or a bare video id).</summary>
    /// <param name="input">The user's text; already trimmed by the caller.</param>
    public static YouTubeLink Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return default;
        string text = input.Trim();

        if (IsVideoId(text)) return new YouTubeLink(YouTubeLinkKind.Video, text, null);

        if (!TryParseUri(text, out Uri? uri)) return default;
        if (!IsYouTubeHost(uri.Host)) return default;

        string host = uri.Host.ToLowerInvariant();
        string path = uri.AbsolutePath;

        // youtu.be/<id>
        if (host is "youtu.be")
        {
            string first = FirstSegment(path);
            return IsVideoId(first) ? new YouTubeLink(YouTubeLinkKind.Video, first, null) : default;
        }

        // /watch?v=, /watch_popup?v=, /movie?v=  (also the legacy #!v= fragment form)
        string? v = QueryValue(uri, "v") ?? FragmentValue(uri, "v");
        if (IsVideoId(v) && (path is "/watch" or "/watch_popup" or "/movie" || path.Length <= 1))
        {
            return new YouTubeLink(YouTubeLinkKind.Video, v, null);
        }

        // /v/<id>, /e/<id>, /embed/<id>, /shorts/<id>, /live/<id>
        Match m = PathId().Match(path);
        if (m.Success)
        {
            string id = m.Groups["id"].Value;
            if (Array.IndexOf(NotIds, id) < 0) return new YouTubeLink(YouTubeLinkKind.Video, id, null);
        }

        // /embed/live_stream?channel=UC...
        if (path.StartsWith("/embed/live_stream", StringComparison.OrdinalIgnoreCase) &&
            QueryValue(uri, "channel") is { Length: > 0 } channelId)
        {
            return new YouTubeLink(YouTubeLinkKind.ChannelLive, null,
                $"https://www.youtube.com/channel/{Uri.EscapeDataString(channelId)}/live");
        }

        // /@name/live, /c/name/live, /channel/UC.../live, /user/name/live
        if (ChannelLivePath().IsMatch(path))
        {
            return new YouTubeLink(YouTubeLinkKind.ChannelLive, null, $"https://www.youtube.com{path}");
        }

        return default;
    }

    /// <summary>Pulls the video id out of a channel "live" page's HTML.</summary>
    /// <remarks>
    /// <c>"currentVideoEndpoint":{...,"watchEndpoint":{"videoId":"..."}}</c> first (what yt-dlp reads), then
    /// <c>&lt;link rel="canonical"&gt;</c> (what streamlink reads). Neither present means the channel is not live.
    /// </remarks>
    /// <param name="html">The page body.</param>
    /// <returns>The video id, or null when the channel is offline.</returns>
    public static string? ExtractLiveVideoId(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;

        int marker = html.IndexOf("\"currentVideoEndpoint\"", StringComparison.Ordinal);
        if (marker >= 0)
        {
            int watch = html.IndexOf("\"watchEndpoint\"", marker, StringComparison.Ordinal);
            if (watch >= 0)
            {
                string? id = ReadJsonStringMember(html, watch, "\"videoId\"");
                if (IsVideoId(id)) return id;
            }
        }

        Match canonical = Canonical().Match(html);
        if (canonical.Success)
        {
            string id = canonical.Groups["id"].Value;
            if (IsVideoId(id)) return id;
        }

        return null;
    }

    [GeneratedRegex("""<link\s[^>]*rel=["']canonical["'][^>]*href=["'][^"']*[?&]v=(?<id>[0-9A-Za-z_-]{11})""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Canonical();

    /// <summary>Reads <c>"name":"value"</c> starting at <paramref name="from"/>; returns null when absent.</summary>
    private static string? ReadJsonStringMember(string html, int from, string name)
    {
        int at = html.IndexOf(name, from, StringComparison.Ordinal);
        if (at < 0) return null;
        int colon = html.IndexOf(':', at + name.Length);
        if (colon < 0) return null;
        int open = html.IndexOf('"', colon + 1);
        if (open < 0) return null;
        int close = html.IndexOf('"', open + 1);
        return close < 0 ? null : html[(open + 1)..close];
    }

    private static bool TryParseUri(string text, [NotNullWhen(true)] out Uri? uri)
    {
        string candidate = text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text;
        return Uri.TryCreate(candidate, UriKind.Absolute, out uri) &&
               (uri.Scheme is "http" or "https");
    }

    private static bool IsYouTubeHost(string host)
    {
        host = host.ToLowerInvariant();
        foreach (string known in Hosts)
        {
            if (host == known || host.EndsWith("." + known, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static string FirstSegment(string path)
    {
        string trimmed = path.Trim('/');
        int slash = trimmed.IndexOf('/');
        return slash < 0 ? trimmed : trimmed[..slash];
    }

    /// <summary>Reads one query parameter without pulling in <c>System.Web</c>.</summary>
    internal static string? QueryValue(Uri uri, string key) => ValueFrom(uri.Query.TrimStart('?'), key);

    private static string? FragmentValue(Uri uri, string key) => ValueFrom(uri.Fragment.TrimStart('#', '!'), key);

    private static string? ValueFrom(string raw, string key)
    {
        if (raw.Length == 0) return null;
        foreach (Range r in raw.AsSpan().Split('&'))
        {
            ReadOnlySpan<char> pair = raw.AsSpan()[r];
            int eq = pair.IndexOf('=');
            if (eq < 0) continue;
            if (!pair[..eq].SequenceEqual(key)) continue;
            return Uri.UnescapeDataString(pair[(eq + 1)..].ToString());
        }

        return null;
    }
}
