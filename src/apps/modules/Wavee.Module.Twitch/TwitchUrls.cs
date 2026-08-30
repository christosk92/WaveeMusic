using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Wavee.Module.Twitch;

/// <summary>What a pasted Twitch link turned out to be.</summary>
public enum TwitchLinkKind
{
    /// <summary>Not a Twitch link this module plays (including clips, which v1 rejects).</summary>
    None = 0,

    /// <summary>A channel's live broadcast.</summary>
    Live,

    /// <summary>An archived broadcast (VOD).</summary>
    Vod,
}

/// <summary>
/// Pure URL → (login | vodId) parsing, kept separate from the module so the whole matching table is unit-testable
/// without HTTP. Mirrors streamlink 8.5's accepted forms.
/// </summary>
public static partial class TwitchUrls
{
    /// <summary>The <c>live:</c> playable-id prefix.</summary>
    public const string LivePrefix = "live:";

    /// <summary>The <c>vod:</c> playable-id prefix.</summary>
    public const string VodPrefix = "vod:";

    private static readonly string[] Hosts =
    [
        "twitch.tv", "www.twitch.tv", "m.twitch.tv", "go.twitch.tv", "player.twitch.tv", "clips.twitch.tv",
    ];

    /// <summary>Path roots that are twitch.tv site pages, never channel logins.</summary>
    private static readonly string[] ReservedLogins =
    [
        "videos", "video", "v", "clip", "clips", "directory", "settings", "downloads", "jobs", "p", "subs",
        "store", "friends", "following", "products", "team", "turbo", "prime", "search", "wallet", "drops",
    ];

    [GeneratedRegex("^[A-Za-z0-9_]{2,30}$")]
    private static partial Regex LoginShape();

    [GeneratedRegex(@"^/(?:videos/|video/|[^/]+/v(?:ideo)?/)(?<id>\d+)/?$")]
    private static partial Regex VodPath();

    /// <summary>True when <paramref name="text"/> has the shape of a channel login.</summary>
    /// <param name="text">The candidate login.</param>
    public static bool IsLogin(string? text) => text is not null && LoginShape().IsMatch(text);

    /// <summary>Parses a pasted link into a playable id.</summary>
    /// <param name="input">The user's text; already trimmed by the caller.</param>
    /// <param name="playableId">The <c>live:&lt;login&gt;</c> or <c>vod:&lt;id&gt;</c> id when matched.</param>
    /// <returns>What the link is.</returns>
    public static TwitchLinkKind Parse(string? input, out string? playableId)
    {
        playableId = null;
        if (string.IsNullOrWhiteSpace(input)) return TwitchLinkKind.None;

        if (!TryParseUri(input.Trim(), out Uri? uri)) return TwitchLinkKind.None;

        string host = uri.Host.ToLowerInvariant();
        if (Array.IndexOf(Hosts, host) < 0) return TwitchLinkKind.None;

        string path = uri.AbsolutePath;

        // Clips are out of scope for v1 (a clip is a short MP4 behind a different GQL query).
        if (host is "clips.twitch.tv" || path.Contains("/clip/", StringComparison.OrdinalIgnoreCase))
        {
            return TwitchLinkKind.None;
        }

        if (host is "player.twitch.tv")
        {
            if (QueryValue(uri, "video") is { Length: > 0 } v)
            {
                string digits = v.TrimStart('v', 'V');
                return Digits(digits) ? Vod(digits, out playableId) : TwitchLinkKind.None;
            }

            if (QueryValue(uri, "channel") is { Length: > 0 } c && LoginShape().IsMatch(c))
            {
                return Live(c, out playableId);
            }

            return TwitchLinkKind.None;
        }

        // /schedule?vodID=123
        if (QueryValue(uri, "vodID") is { Length: > 0 } scheduled && Digits(scheduled))
        {
            return Vod(scheduled, out playableId);
        }

        Match vod = VodPath().Match(path);
        if (vod.Success) return Vod(vod.Groups["id"].Value, out playableId);

        string login = path.Trim('/');
        if (login.Length == 0 || login.Contains('/', StringComparison.Ordinal)) return TwitchLinkKind.None;
        if (Array.IndexOf(ReservedLogins, login.ToLowerInvariant()) >= 0) return TwitchLinkKind.None;
        if (!LoginShape().IsMatch(login)) return TwitchLinkKind.None;

        return Live(login, out playableId);
    }

    /// <summary>Splits a playable id back into its kind and payload.</summary>
    /// <param name="playableId">A <c>live:&lt;login&gt;</c> or <c>vod:&lt;id&gt;</c> id.</param>
    /// <param name="value">The login (live) or numeric id (vod).</param>
    /// <returns>What the id names.</returns>
    public static TwitchLinkKind Split(string? playableId, out string value)
    {
        value = string.Empty;
        if (playableId is null) return TwitchLinkKind.None;

        if (playableId.StartsWith(LivePrefix, StringComparison.Ordinal))
        {
            value = playableId[LivePrefix.Length..];
            return LoginShape().IsMatch(value) ? TwitchLinkKind.Live : TwitchLinkKind.None;
        }

        if (playableId.StartsWith(VodPrefix, StringComparison.Ordinal))
        {
            value = playableId[VodPrefix.Length..];
            return Digits(value) ? TwitchLinkKind.Vod : TwitchLinkKind.None;
        }

        return TwitchLinkKind.None;
    }

    private static TwitchLinkKind Live(string login, out string? playableId)
    {
        playableId = LivePrefix + login.ToLowerInvariant();
        return TwitchLinkKind.Live;
    }

    private static TwitchLinkKind Vod(string id, out string? playableId)
    {
        playableId = VodPrefix + id;
        return TwitchLinkKind.Vod;
    }

    private static bool Digits(string s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s)
        {
            if (c is < '0' or > '9') return false;
        }

        return true;
    }

    private static bool TryParseUri(string text, [NotNullWhen(true)] out Uri? uri)
    {
        string candidate = text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text;
        return Uri.TryCreate(candidate, UriKind.Absolute, out uri) && uri.Scheme is "http" or "https";
    }

    /// <summary>Reads one query parameter without pulling in <c>System.Web</c>.</summary>
    /// <param name="uri">The url to read.</param>
    /// <param name="key">The parameter name.</param>
    internal static string? QueryValue(Uri uri, string key)
    {
        string raw = uri.Query.TrimStart('?');
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
