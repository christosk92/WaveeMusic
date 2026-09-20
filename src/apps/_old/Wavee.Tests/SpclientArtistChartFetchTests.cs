using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Spotify;
using Wavee.SpotifyLive.Hydration;
using Xunit;

namespace Wavee.Tests;

// ── The chart port's best-effort contract, made true (design §2.2) ───────────────────────────────────────────────────
// The class comment promises "a non-2xx or an unparsable body is an EMPTY list plus a structured event — never an
// exception that could blank a painted chart". The 2xx half was enforced; the PARSE half was not, so a 200 carrying an
// HTML error page (a captive portal, an edge node's 200-with-a-body-of-apology) threw JsonException straight through
// the artist ladder into the provider hydrator's catch-all, turning the whole batch Failed. These pin both halves.
public class SpclientArtistChartFetchTests
{
    const string ArtistUri = "spotify:artist:ar1";

    sealed class ScriptedExchange(int status, byte[] body) : IHttpExchange
    {
        public string? LastUrl;
        public Task<HttpResp> SendAsync(HttpReq req, CancellationToken ct)
        {
            LastUrl = req.Url;
            return Task.FromResult(new HttpResp(status,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), body));
        }
    }

    static SpclientArtistChartFetch Fetch(IHttpExchange http) => new(http, () => "https://spclient.test");

    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task AnHtmlBodyBehindA200_IsAnEmptyList_NotAThrow()
    {
        var fetch = Fetch(new ScriptedExchange(200, Utf8("<html><body>502 Bad Gateway</body></html>")));

        Assert.Empty(await fetch.TopTrackUrisAsync(ArtistUri, CancellationToken.None));
    }

    [Fact]
    public async Task ATruncatedJsonBody_IsAnEmptyList_NotAThrow()
    {
        var fetch = Fetch(new ScriptedExchange(200, Utf8("{\"tracks\":[{\"uri\":\"spotify:track:a\"")));

        Assert.Empty(await fetch.TopTrackUrisAsync(ArtistUri, CancellationToken.None));
    }

    [Fact]
    public async Task AnEmptyBody_IsAnEmptyList_NotAThrow()
    {
        var fetch = Fetch(new ScriptedExchange(200, Array.Empty<byte>()));

        Assert.Empty(await fetch.TopTrackUrisAsync(ArtistUri, CancellationToken.None));
    }

    [Fact]
    public async Task ANonSuccessStatus_IsAnEmptyList()
    {
        var fetch = Fetch(new ScriptedExchange(503, Utf8("nope")));

        Assert.Empty(await fetch.TopTrackUrisAsync(ArtistUri, CancellationToken.None));
    }

    [Fact]
    public async Task AWellFormedBody_YieldsItsUris()
    {
        // The happy path stays intact — the guard must not have swallowed the answer with the errors.
        var http = new ScriptedExchange(200, Utf8(
            "{\"tracks\":[{\"uri\":\"spotify:track:a\"},{\"uri\":\"spotify:track:b\"},{\"noUri\":1}]}"));

        var uris = await Fetch(http).TopTrackUrisAsync(ArtistUri, CancellationToken.None);

        Assert.Equal(["spotify:track:a", "spotify:track:b"], uris);
        Assert.Contains("artist-top-tracks-extensions", http.LastUrl);
    }

    [Fact]
    public async Task ANonSpotifyArtist_NeverReachesTheTransport()
    {
        var http = new ScriptedExchange(200, Utf8("{}"));

        Assert.Empty(await Fetch(http).TopTrackUrisAsync("local:artist:a1", CancellationToken.None));
        Assert.Null(http.LastUrl);
    }
}
