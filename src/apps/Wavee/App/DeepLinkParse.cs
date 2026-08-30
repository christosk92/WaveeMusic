using System;
using Wavee.Core;

namespace Wavee;

// The PURE half of the deep-link surface: raw activation string → DeepLinkVerb. It lives in its own file, with no
// FluentGpu/Win32 usings at all, for one reason — Wavee.Tests source-includes this file and cannot reference the
// engine. The window-waking, scheme-registration and channel halves stay in DeepLink.cs.

/// <summary>A parsed <c>wavee://</c> verb. Unknown or garbage input never produces one (the parser never throws).</summary>
/// <param name="Link">A pasted link for the playback modules (<c>wavee://play?link=…</c>): a YouTube / Twitch /
/// radio url the module router resolves, as opposed to <paramref name="Context"/>, a Spotify context uri. Exactly one
/// of the two is set on a <see cref="DeepLinkKind.Play"/> verb.</param>
public readonly record struct DeepLinkVerb(DeepLinkKind Kind, string Route, string Arg, string Context, string Link = "");

/// <summary>The <c>wavee://</c> verbs. Navigation keys are opaque strings the shell owns — see the skill doc.</summary>
public enum DeepLinkKind : byte { Open, Play, Resume, Pause }

public static partial class DeepLink
{
    /// <summary>Parse <paramref name="raw"/> as a <c>wavee://</c> verb. Accepts a bare URI or a command line that
    /// contains one. Percent-encoding is decoded. Returns <c>false</c> for unknown verbs, missing required args, or
    /// garbage — never throws.</summary>
    public static bool TryParse(string? raw, out DeepLinkVerb verb)
    {
        verb = default;
        if (TryParseSpotifyUri(raw, out verb)) return true;
        if (!TryExtractUri(raw, out string text)) return false;
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)) return false;
        if (!string.Equals(uri.Scheme, "wavee", StringComparison.OrdinalIgnoreCase)) return false;

        string name = uri.Host;
        if (name.Length == 0)
        {
            string path = uri.AbsolutePath.Trim('/');
            int slash = path.IndexOf('/');
            name = slash < 0 ? path : path[..slash];
        }
        if (name.Length == 0) return false;

        ReadQuery(uri.Query, out string route, out string arg, out string ctx, out string link);

        if (name.Equals("open", StringComparison.OrdinalIgnoreCase))
        {
            if (route.Length == 0) return false;
            verb = new DeepLinkVerb(DeepLinkKind.Open, route, arg, "");
            return true;
        }
        if (name.Equals("play", StringComparison.OrdinalIgnoreCase))
        {
            if (ctx.Length > 0) { verb = new DeepLinkVerb(DeepLinkKind.Play, "", "", ctx); return true; }
            // `link=` is the module path: the same intake the Play ▸ Link… dialog feeds (YouTube, Twitch, radio…).
            if (link.Length > 0 && PlayLinkActions.LooksLikeUrl(link))
            { verb = new DeepLinkVerb(DeepLinkKind.Play, "", "", "", link); return true; }
            return false;
        }
        if (name.Equals("resume", StringComparison.OrdinalIgnoreCase))
        {
            verb = new DeepLinkVerb(DeepLinkKind.Resume, "", "", "");
            return true;
        }
        if (name.Equals("pause", StringComparison.OrdinalIgnoreCase))
        {
            verb = new DeepLinkVerb(DeepLinkKind.Pause, "", "", "");
            return true;
        }
        return false;
    }

    /// <summary>Translate a bare Spotify entity URI into the equivalent Wavee verb, so the opt-in <c>spotify:</c> handler
    /// (<c>WaveeSettings.HandleSpotifyLinks</c>) can share ONE activation path with <c>wavee://</c>. Pages become
    /// <see cref="DeepLinkKind.Open"/> on the shell's own route names; a PLAYABLE (a track or a podcast episode) becomes
    /// <see cref="DeepLinkKind.Play"/> (which is what clicking a shared link to one means) and therefore rides the
    /// context resolver like any other play.
    /// Everything else — users, concerts, search links, <c>https://open.spotify.com/…</c> web links (those go to the
    /// browser, not to us) — is refused rather than guessed at.</summary>
    static bool TryParseSpotifyUri(string? raw, out DeepLinkVerb verb)
    {
        verb = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        ReadOnlySpan<char> s = raw.AsSpan().Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1].Trim();
        if (!s.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase)) return false;

        // spotify:<kind>:<id> — reject the nested/extra-segment forms (spotify:user:x:playlist:y) rather than guessing.
        ReadOnlySpan<char> rest = s["spotify:".Length..];
        int colon = rest.IndexOf(':');
        if (colon <= 0 || colon + 1 >= rest.Length) return false;
        ReadOnlySpan<char> kind = rest[..colon];
        ReadOnlySpan<char> id = rest[(colon + 1)..];
        if (id.IndexOf(':') >= 0 || id.IndexOfAny(' ', '\t', '"') >= 0) return false;

        // The shape above is this parser's own business (quotes, casing, the nested forms it refuses); WHICH entity the
        // uri addresses is EntityUri's — so normalize the scheme + kind to lower case (the id stays verbatim: base62 IS
        // case-sensitive, and the store only ever keys the lower-case uri) and let the one parser answer the kind.
        string uri = string.Concat("spotify:", kind.ToString().ToLowerInvariant(), ":", id.ToString());
        var entityKind = EntityUri.KindOf(uri);
        // A PLAYABLE is a track OR an episode: clicking a shared podcast-episode link means "play this" exactly the way
        // a shared track link does, and the play path below is uri-generic (the context resolver takes the uri as the
        // context, and /context-resolve answers for an episode). Gating this on Track alone is why a shared episode
        // link fell through to "route is null ⇒ refuse" and did nothing at all.
        if (entityKind is EntityKind.Track or EntityKind.Episode)
        {
            verb = new DeepLinkVerb(DeepLinkKind.Play, "", "", uri);
            return true;
        }
        string? route = entityKind switch
        {
            EntityKind.Album => "album",
            EntityKind.Playlist => "pl",
            EntityKind.Artist => "artist",
            EntityKind.Show => "show",
            _ => null,
        };
        if (route is null) return false;
        verb = new DeepLinkVerb(DeepLinkKind.Open, route, uri, "");
        return true;
    }

    static bool TryExtractUri(string? raw, out string uri)
    {
        uri = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        ReadOnlySpan<char> s = raw.AsSpan().Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1].Trim();
        if (StartsWithWavee(s))
        {
            uri = s.ToString();
            return true;
        }
        int i = IndexOfWavee(raw);
        if (i < 0) return false;
        int end = i + 1;
        while (end < raw.Length && !char.IsWhiteSpace(raw[end]) && raw[end] != '"') end++;
        uri = raw[i..end];
        return uri.Length > 0;
    }

    static bool StartsWithWavee(ReadOnlySpan<char> s)
        => s.StartsWith("wavee:", StringComparison.OrdinalIgnoreCase);

    static int IndexOfWavee(string raw)
    {
        int i = raw.IndexOf("wavee://", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? i : raw.IndexOf("wavee:", StringComparison.OrdinalIgnoreCase);
    }

    static void ReadQuery(string query, out string route, out string arg, out string ctx, out string link)
    {
        route = arg = ctx = link = "";
        if (query.Length == 0) return;
        ReadOnlySpan<char> q = query;
        if (q[0] == '?') q = q[1..];
        while (q.Length > 0)
        {
            int amp = q.IndexOf('&');
            ReadOnlySpan<char> pair = amp < 0 ? q : q[..amp];
            q = amp < 0 ? default : q[(amp + 1)..];
            if (pair.Length == 0) continue;
            int eq = pair.IndexOf('=');
            string key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).ToString());
            string val = eq < 0 || eq + 1 >= pair.Length ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].ToString());
            if (key.Equals("route", StringComparison.OrdinalIgnoreCase)) route = val;
            else if (key.Equals("arg", StringComparison.OrdinalIgnoreCase)) arg = val;
            else if (key.Equals("ctx", StringComparison.OrdinalIgnoreCase)) ctx = val;
            else if (key.Equals("link", StringComparison.OrdinalIgnoreCase)) link = val;
        }
    }
}
