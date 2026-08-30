using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;

namespace Wavee.SpotifyLive;

/// <summary>
/// Resolves a Spotify music-video <c>manifest_id</c> to a parsed <see cref="SpotifyVideoManifest"/> over the app's
/// authenticated transport: <c>GET /manifests/v9/json/sources/{manifestId}/options/supports_drm</c> with the CORS-fenced
/// xpui <c>Origin</c>/<c>Referer</c> (the same request shape <see cref="Audio.AudioFormatProbe"/> already proves). The
/// <c>manifest_id</c> itself is <c>Convert.ToHexStringLower(videoTrack.OriginalVideo[0].Gid)</c> — resolved by the video
/// service from a track's video association (a follow-up factoring of the probe's discovery logic).
/// </summary>
static class SpotifyVideoResolver
{
    const string Xpui = "https://xpui.app.spotify.com";

    public static async Task<SpotifyVideoManifest?> ResolveManifestAsync(ITransport transport, string manifestId, CancellationToken ct = default)
    {
        var (status, json) = await ResolveManifestJsonAsync(transport, manifestId, ct).ConfigureAwait(false);
        if (status is < 200 or >= 300 || json.Length == 0) return null;

        try { return SpotifyVideoManifest.FromJson(json); }
        catch { return null; }   // a malformed/absent manifest → no video (never a throw into playback)
    }

    /// <summary>The RAW manifest fetch — the ONE place the route + CORS-fenced headers are defined (the parsed
    /// <see cref="ResolveManifestAsync"/> rides on it). Returns the HTTP status and the body exactly as served, so a
    /// diagnostic (<c>--spotify-video-manifest</c>) can inspect fields the parsed model deliberately drops (audio-only
    /// profiles). Status 0 = the request never completed (no transport / threw); never throws except on cancellation.</summary>
    public static async Task<(int Status, string Json)> ResolveManifestJsonAsync(ITransport transport, string manifestId, CancellationToken ct = default)
    {
        if (transport is null || string.IsNullOrWhiteSpace(manifestId)) return (0, "");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "*/*",
            ["Origin"] = Xpui,
            ["Referer"] = Xpui + "/",
        };

        Resp resp;
        try
        {
            resp = await transport.Request(Channel.Spclient, ManifestRoute(manifestId), default, ct, method: "GET", headers: headers).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (0, "");
        }
        return (resp.Status, resp.Body is { Length: > 0 } body ? Encoding.UTF8.GetString(body) : "");
    }

    /// <summary>The v9 manifest route for <paramref name="manifestId"/> (one definition; the probe logs it verbatim).</summary>
    public static string ManifestRoute(string manifestId) => "/manifests/v9/json/sources/" + manifestId + "/options/supports_drm";
}
