using System;
using System.Collections.Generic;

using Wavee.Backend.Audio;

namespace Wavee.Backend.Spotify;

// Desktop client-identity constants Spotify expects on spclient requests. Version strings are hardcoded in
// SpotifyRuntimeIdentity until manifest-driven pins land.
public static class SpotifyHeaders
{
    /// <summary>Spotify's public desktop client id. Single owner: <see cref="SpotifyRuntimeIdentity.KeymasterClientId"/>.</summary>
    public const string ClientId = SpotifyRuntimeIdentity.KeymasterClientId;
    public static string ClientVersion => SpotifyRuntimeIdentityHost.Current.ClientVersion;
    public static string AppPlatform => SpotifyRuntimeIdentity.AppPlatform;
    public static string AppVersion => SpotifyRuntimeIdentityHost.Current.AppVersion;
    public static string UserAgent => SpotifyRuntimeIdentityHost.Current.UserAgent;
    /// <summary>Normalizes Spotify's preferred-language value to the two-letter form used by the desktop client.</summary>
    public static string NormalizeLanguage(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture)) return "en";
        ReadOnlySpan<char> value = culture.AsSpan().Trim();
        int separator = value.IndexOfAny('-', '_');
        ReadOnlySpan<char> language = separator >= 0 ? value[..separator] : value;
        return language.Length == 2 && char.IsAsciiLetter(language[0]) && char.IsAsciiLetter(language[1])
            ? language.ToString().ToLowerInvariant()
            : "en";
    }

    // ── §2.7 — the first-party header set for the playlist-v2 MUTATION routes (/…/changes, /…/rootlist/changes) ──
    // The gateway gates these routes on a matching (Spotify-App-Version · App-Platform · User-Agent · spotify-playlist-
    // sync-reason) tuple: a request missing them 200-OKs against a PASSIVE read handler that never mutates state — the
    // silent-no-op class this fixes. Content-Type MUST be x-www-form-urlencoded despite the binary protobuf body (anything
    // else routes to the wrong handler). Bearer + client-token + User-Agent are stamped by the HTTP middleware
    // (AuthMiddleware / ClientTokenMiddleware), so they are NOT duplicated here; App-Platform / Spotify-App-Version are
    // repeated defensively (the middleware overwrites them with the same values). Origin is intentionally omitted — it is
    // not part of the gateway's gating tuple, and the spclient base URL isn't available at this layer (the transport owns
    // URL composition).
    public static Dictionary<string, string> PlaylistV2Mutation(string language, string? spclientBaseUrl = null)
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/x-www-form-urlencoded",
            ["App-Platform"] = AppPlatform,
            ["Spotify-App-Version"] = AppVersion,
            ["spotify-playlist-sync-reason"] = "CAk=",
            ["spotify-apply-lenses"] = "auto",
            ["Accept-Language"] = NormalizeLanguage(language),
            ["Cache-Control"] = "no-store",
            ["spotify-accept-geoblock"] = "dummy",
            ["spotify-dsa-mode-enabled"] = "false",
        }.AlsoOrigin(spclientBaseUrl);

    // ── The playlist4 LIST-READ family for GET /playlist/v2/list/recents/page[/diff] ─────────────────────────────────
    // A playlist4 LIST read (recents, podcast-chapters, …) is gated on the same client-identity tuple as the mutation
    // routes, but adds two list-specific headers the desktop client sends: `x-accept-list-items` (the item types the
    // client accepts in the list) and `spotify-playlist-sync-reason` — CAwQAQ== on the COLD page load, CAEQAQ== on a
    // refresh/diff. The six always-on identity headers (Bearer, client-token, App-Platform, Spotify-App-Version,
    // User-Agent, Accept-Language) are stamped by the HTTP middleware; only the list family is set here.
    public static Dictionary<string, string> RecentsList(bool diff)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/protobuf",
            ["spotify-playlist-sync-reason"] = diff ? "CAEQAQ==" : "CAwQAQ==",
            ["spotify-apply-lenses"] = "auto",
            ["x-accept-list-items"] = "audio-track, audio-episode, video-episode, audiobook",
        };
        if (diff) headers["spotify-applied-lenses"] = "auto";   // the diff call echoes the applied lens set
        return headers;
    }

    // ── the CREATE flavour of the mutation header set ────────────────────────────────────────────────────────────────
    // Desktop creates a playlist by POSTing a /changes body to the CLIENT-MINTED id (there is no create endpoint any
    // more), and the only header that differs from an ordinary edit is the sync reason: CAw= (12) instead of CAk= (9).
    public static Dictionary<string, string> PlaylistV2Create(string language, string? spclientBaseUrl = null)
    {
        var headers = PlaylistV2Mutation(language, spclientBaseUrl);
        headers["spotify-playlist-sync-reason"] = "CAw=";
        return headers;
    }

    /// <summary>Captured desktop header tuple for POST <c>/playlist/v2/playlist/{id}/signals</c>.</summary>
    public static Dictionary<string, string> PlaylistSignals(string language, string? spclientBaseUrl = null)
    {
        var headers = PlaylistV2Mutation(language, spclientBaseUrl);
        headers["Accept"] = "application/x-protobuf";
        headers["spotify-playlist-sync-reason"] = "CA8QAQ==";
        return headers;
    }

    static Dictionary<string, string> AlsoOrigin(this Dictionary<string, string> h, string? baseUrl)
    {
        if (!string.IsNullOrEmpty(baseUrl)) h["Origin"] = baseUrl.TrimEnd('/');
        return h;
    }

    // ── PlayPlay Step A — POST /playplay/v1/key/{fileIdHex} ───────────────────────────────────────────────────────────
    // Same gateway quirk as playlist-v2 mutations: Content-Type MUST be x-www-form-urlencoded despite a protobuf body.
    // Bearer, client-token, App-Platform, Spotify-App-Version, and User-Agent are stamped by ClientTokenMiddleware.
    public static Dictionary<string, string> PlayPlayKey(string language)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/x-www-form-urlencoded",
            ["Accept-Language"] = NormalizeLanguage(language),
        };

    // Compatibility for optional external PlayPlay source trees that have not yet adopted launch-locale injection.
    public static Dictionary<string, string> PlayPlayKey() => PlayPlayKey("en");
}
