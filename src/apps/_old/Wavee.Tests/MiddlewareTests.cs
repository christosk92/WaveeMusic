using Wavee.Backend.Audio;
using Wavee.Backend.Spotify;
using Xunit;

namespace Wavee.Tests;

public class HttpMiddlewareTests
{
    static readonly Dictionary<string, string> NoHeaders = new();
    static HttpReq Req => new("GET", "https://spclient/metadata", NoHeaders);
    static HttpResp Ok(string token = "") => new(200, new Dictionary<string, string> { ["echo-token"] = token }, []);

    [Fact]
    public async Task Auth_AttachesBearer_AndOn401_RefreshesAndRetries()
    {
        // first call sees the "old" token and returns 401; after refresh ("new"), 200.
        var tokens = new Queue<string>(["old", "new"]);
        var auth = new AuthMiddleware((force, _) => Task.FromResult(tokens.Dequeue()));

        var inner = new FakeExchange((req, call) =>
        {
            var bearer = req.Headers["Authorization"];
            return bearer.EndsWith("old") ? new HttpResp(401, NoHeaders, []) : Ok(bearer);
        });
        var pipe = new HttpPipeline(inner, auth);

        var resp = await pipe.SendAsync(Req, TestContext.Current.CancellationToken);

        Assert.Equal(200, resp.Status);
        Assert.Equal(1, auth.Refreshes);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Auth_Success_PassesThroughWithoutRefresh()
    {
        var auth = new AuthMiddleware((force, _) => Task.FromResult("tok"));
        var inner = new FakeExchange((_, _) => Ok());
        var resp = await new HttpPipeline(inner, auth).SendAsync(Req, TestContext.Current.CancellationToken);
        Assert.Equal(200, resp.Status);
        Assert.Equal(0, auth.Refreshes);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task RateLimit_On429_BacksOff_ThenSucceeds_HonoringRetryAfter()
    {
        var delays = new List<TimeSpan>();
        var rate = new RateLimitMiddleware((d, _) => { delays.Add(d); return Task.CompletedTask; });

        var inner = new FakeExchange((_, call) => call == 1
            ? new HttpResp(429, new Dictionary<string, string> { ["Retry-After"] = "7" }, [])
            : Ok());
        var resp = await new HttpPipeline(inner, rate).SendAsync(Req, TestContext.Current.CancellationToken);

        Assert.Equal(200, resp.Status);
        Assert.Equal(1, rate.Backoffs);
        Assert.Single(delays);
        Assert.Equal(TimeSpan.FromSeconds(7), delays[0]);   // honored Retry-After
    }

    [Fact]
    public async Task Pipeline_OrdersAuthOutsideRateLimit()
    {
        // 429 (retried by rate-limit) then 401 (refreshed by auth) then 200 — both middlewares cooperate.
        var auth = new AuthMiddleware((force, _) => Task.FromResult("t"));
        var rate = new RateLimitMiddleware((_, _) => Task.CompletedTask);
        var inner = new FakeExchange((_, call) => call switch
        {
            1 => new HttpResp(429, NoHeaders, []),
            2 => new HttpResp(401, NoHeaders, []),
            _ => Ok(),
        });
        var resp = await new HttpPipeline(inner, auth, rate).SendAsync(Req, TestContext.Current.CancellationToken);
        Assert.Equal(200, resp.Status);
        Assert.Equal(1, auth.Refreshes);
        Assert.Equal(1, rate.Backoffs);
    }

    [Fact]
    public async Task ClientToken_AttachesTokenAndDesktopHeaders()
    {
        var ctm = new ClientTokenMiddleware(_ => Task.FromResult<string?>("CT123"), "nl-NL");
        HttpReq? captured = null;
        var inner = new FakeExchange((req, _) => { captured = req; return Ok(); });

        var resp = await new HttpPipeline(inner, ctm).SendAsync(Req, TestContext.Current.CancellationToken);

        Assert.Equal(200, resp.Status);
        Assert.Equal("CT123", captured!.Headers["client-token"]);
        Assert.Equal(SpotifyHeaders.AppPlatform, captured.Headers["App-Platform"]);
        // Read the pin rather than restating it: this test asserts the middleware ATTACHES the app version, which is
        // orthogonal to what that version currently is (SpotifyRuntimeIdentityTests owns the pin's value).
        Assert.Equal(SpotifyRuntimeIdentity.DefaultAppVersion, captured.Headers["Spotify-App-Version"]);
        Assert.StartsWith("Spotify/", captured.Headers["User-Agent"]);
        Assert.Equal("nl", captured.Headers["Accept-Language"]);
    }

    [Fact]
    public async Task ClientToken_NullToken_OmitsHeader_ButKeepsDesktopHeaders()
    {
        var ctm = new ClientTokenMiddleware(_ => Task.FromResult<string?>(null));
        HttpReq? captured = null;
        var inner = new FakeExchange((req, _) => { captured = req; return Ok(); });

        await new HttpPipeline(inner, ctm).SendAsync(Req, TestContext.Current.CancellationToken);

        Assert.False(captured!.Headers.ContainsKey("client-token"));   // null token → header omitted, not "Bearer null"
        Assert.Equal(SpotifyHeaders.AppPlatform, captured.Headers["App-Platform"]);
    }

    [Fact]
    public async Task PathfinderHeaders_AttachesWebPlayerIdentityAndClientToken()
    {
        var mw = new PathfinderHeadersMiddleware(_ => Task.FromResult<string?>("CT"), "ko-KR");
        HttpReq? captured = null;
        var inner = new FakeExchange((req, _) => { captured = req; return Ok(); });
        var req = new HttpReq("POST", "https://api-partner.spotify.com/pathfinder/v2/query",
            new Dictionary<string, string>
            {
                [PathfinderHeadersMiddleware.PlatformHeader] = PathfinderHeadersMiddleware.WebPlayerPlatform,
            },
            []);

        await new HttpPipeline(inner, mw).SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal("CT", captured!.Headers["client-token"]);
        Assert.Equal("WebPlayer", captured.Headers["app-platform"]);
        Assert.Contains("Chrome/147.0.0.0", captured.Headers["user-agent"]);
        Assert.Equal("application/json", captured.Headers["content-type"]);
        Assert.Equal("ko", captured.Headers["accept-language"]);
        Assert.False(captured.Headers.ContainsKey(PathfinderHeadersMiddleware.PlatformHeader));
    }

    [Theory]
    [InlineData("nl-NL", "nl")]
    [InlineData("KO_kr", "ko")]
    [InlineData("en", "en")]
    [InlineData("not-a-language", "en")]
    [InlineData(null, "en")]
    public void SpotifyLanguage_NormalizesToDesktopTwoLetterValue(string? input, string expected)
        => Assert.Equal(expected, SpotifyHeaders.NormalizeLanguage(input));
}

public class ApResolverTests
{
    [Fact]
    public void Parse_ExtractsAccessPoints()
    {
        const string json = """{"accesspoint":["ap-gae2.spotify.com:4070","ap.spotify.com:443"],"dealer":["x"]}""";
        var hosts = ApResolver.ParseAccessPoints(json);
        Assert.Equal(2, hosts.Count);
        Assert.Equal("ap-gae2.spotify.com:4070", hosts[0]);
    }

    [Fact]
    public void Parse_MissingKey_ReturnsEmpty()
    {
        Assert.Empty(ApResolver.ParseAccessPoints("""{"dealer":["x"]}"""));
    }
}
