using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentGpu.Media;
using Wavee.Backend;

namespace Wavee.SpotifyLive;

/// <summary>
/// The ONLY Spotify-aware line of the DRM path: the <c>MediaPlayerBuilder.WithDrm</c> relay. The native PlayReady CDM
/// raises a SOAP challenge; this POSTs it to Spotify's webgate PlayReady license endpoint over the app's authenticated
/// <see cref="ITransport"/> (Bearer + client-token are added by the transport) with the CORS-fenced xpui Origin/Referer,
/// and returns the license bytes. The engine's <c>DrmLicenseBridge</c> + native CDM stay Spotify-agnostic — they only
/// see an opaque challenge out and license blob in; the content key never crosses into managed code.
/// </summary>
static class SpotifyLicenseRelay
{
    const string Xpui = "https://xpui.app.spotify.com";
    const string DefaultEndpoint = "/playready-license";
    const string AcquireLicenseSoapAction = "\"http://schemas.microsoft.com/DRM/2007/03/protocols/AcquireLicense\"";

    /// <summary>Build a relay POSTing to <paramref name="licenseEndpoint"/> (from the manifest's playready
    /// <c>license_server_endpoint</c>) or the default <c>/playready-license</c>.</summary>
    public static Func<LicenseRequest, ValueTask<LicenseResponse>> Create(ITransport transport, string? licenseEndpoint = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        string route = ResolveRoute(licenseEndpoint);
        return async req =>
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "text/xml; charset=utf-8",
                ["SOAPAction"] = AcquireLicenseSoapAction,
                ["Origin"] = Xpui,
                ["Referer"] = Xpui + "/",
            };
            Resp resp = await transport.Request(Channel.Spclient, route, req.Challenge, default, method: "POST", headers: headers)
                .ConfigureAwait(false);
            if (!resp.Ok || resp.Body is null || resp.Body.Length == 0)
                throw new InvalidOperationException($"Spotify PlayReady license POST to {route} failed (HTTP {resp.Status}).");
            return new LicenseResponse(resp.Body);
        };
    }

    // The manifest endpoint may be a relative path, an absolute URL, or an "@webgate"-style prefix; the transport prefixes
    // the resolved spclient host, so reduce everything to a path.
    static string ResolveRoute(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return DefaultEndpoint;
        endpoint = endpoint.Trim();
        if (endpoint.StartsWith("/", StringComparison.Ordinal)) return endpoint;
        int scheme = endpoint.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return "/" + endpoint.TrimStart('@', '/');
        int pathStart = endpoint.IndexOf('/', scheme + 3);
        return pathStart >= 0 ? endpoint[pathStart..] : DefaultEndpoint;
    }
}
