using System.Text.Json;
using Wavee.Backend.Audio;

namespace Wavee.Backend.Spotify;

// ── ③ Transport — the HTTPS-channel middleware pipeline (in line with the design's "tokens are middleware, not a Login5
// subsystem"). Every spclient/pathfinder request flows through: bearer-attach + 401-refresh (AuthMiddleware) and
// 429-backoff (RateLimitMiddleware). Deterministically testable through the IHttpExchange seam (no network/clock).

public sealed record HttpReq(string Method, string Url, IReadOnlyDictionary<string, string> Headers, byte[]? Body = null);

/// <summary>A response whose body is a STREAM (not a buffered byte[]) so a multi-MB metadata batch never lands on the LOH —
/// the consumer parses straight from it. Owns the underlying HttpResponseMessage; Dispose frees the connection. A byte[]
/// convenience ctor keeps tests + small bodies simple.</summary>
public sealed class HttpResp : IDisposable
{
    public int Status { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public Stream Body { get; }
    readonly IDisposable? _owner;

    public HttpResp(int status, IReadOnlyDictionary<string, string> headers, Stream body, IDisposable? owner = null)
    {
        Status = status; Headers = headers; Body = body; _owner = owner;
    }

    public HttpResp(int status, IReadOnlyDictionary<string, string> headers, byte[] body)
        : this(status, headers, new MemoryStream(body, writable: false)) { }

    public void Dispose() { if (_owner is not null) _owner.Dispose(); else Body.Dispose(); }
}

public interface IHttpExchange { Task<HttpResp> SendAsync(HttpReq req, CancellationToken ct); }

public interface IHttpMiddleware
{
    Task<HttpResp> InvokeAsync(HttpReq req, Func<HttpReq, CancellationToken, Task<HttpResp>> next, CancellationToken ct);
}

/// <summary>Composes middleware around an inner exchange (outermost-first), like an HTTP delegating-handler chain.</summary>
public sealed class HttpPipeline : IHttpExchange
{
    readonly Func<HttpReq, CancellationToken, Task<HttpResp>> _entry;

    public HttpPipeline(IHttpExchange inner, params IHttpMiddleware[] middleware)
    {
        Func<HttpReq, CancellationToken, Task<HttpResp>> next = inner.SendAsync;
        for (int i = middleware.Length - 1; i >= 0; i--)
        {
            var mw = middleware[i];
            var nxt = next;
            next = (r, c) => mw.InvokeAsync(r, nxt, c);
        }
        _entry = next;
    }

    public Task<HttpResp> SendAsync(HttpReq req, CancellationToken ct) => _entry(req, ct);
}

/// <summary>Attaches the bearer access-token; on 401 refreshes once and retries (the token provider may rotate the token).</summary>
public sealed class AuthMiddleware : IHttpMiddleware
{
    readonly Func<bool, CancellationToken, Task<string>> _token;   // (forceRefresh, ct) → access token
    public int Refreshes { get; private set; }

    public AuthMiddleware(Func<bool, CancellationToken, Task<string>> token) => _token = token;

    public async Task<HttpResp> InvokeAsync(HttpReq req, Func<HttpReq, CancellationToken, Task<HttpResp>> next, CancellationToken ct)
    {
        var resp = await next(WithBearer(req, await _token(false, ct).ConfigureAwait(false)), ct).ConfigureAwait(false);
        if (resp.Status == 401)
        {
            resp.Dispose();   // free the discarded 401 response (its stream/connection) before retrying
            Refreshes++;
            // force a genuinely fresh token — a cached-token provider would otherwise hand back the same rejected one.
            resp = await next(WithBearer(req, await _token(true, ct).ConfigureAwait(false)), ct).ConfigureAwait(false);
        }
        return resp;
    }

    static HttpReq WithBearer(HttpReq r, string token)
    {
        var h = new Dictionary<string, string>(r.Headers) { ["Authorization"] = "Bearer " + token };
        return r with { Headers = h };
    }
}

/// <summary>On 429, waits (Retry-After or 1s) and retries, up to a cap — so a completion-fetch fan-out can't self-DoS.</summary>
public sealed class RateLimitMiddleware : IHttpMiddleware
{
    readonly Func<TimeSpan, CancellationToken, Task> _delay;
    public int Backoffs { get; private set; }

    public RateLimitMiddleware(Func<TimeSpan, CancellationToken, Task>? delay = null)
        => _delay = delay ?? ((d, c) => Task.Delay(d, c));

    public async Task<HttpResp> InvokeAsync(HttpReq req, Func<HttpReq, CancellationToken, Task<HttpResp>> next, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var resp = await next(req, ct).ConfigureAwait(false);
            if (resp.Status != 429) return resp;
            Backoffs++;
            // CLAMP Retry-After to ≤30s — the header is unauthenticated, so a hostile/buggy server saying "86400" must not
            // make the client sleep for a day. (Header dict is OrdinalIgnoreCase, so HTTP/2's lowercase "retry-after" hits.)
            var wait = resp.Headers.TryGetValue("Retry-After", out var ra) && int.TryParse(ra, out var s)
                ? TimeSpan.FromSeconds(Math.Clamp(s, 0, 30)) : TimeSpan.FromSeconds(1);
            resp.Dispose();   // read Retry-After first, THEN free the discarded 429 response
            await _delay(wait, ct).ConfigureAwait(false);
        }
        return await next(req, ct).ConfigureAwait(false);
    }
}

/// <summary>Attaches the spclient <c>client-token</c> (from the attestation client) + the desktop identity headers to every
/// request. The client-token is required — spclient 403s without it — but best-effort here: a null token just omits the
/// header (the attestation client logs the failure). Sits inside AuthMiddleware so both the bearer and client-token ride.</summary>
public sealed class ClientTokenMiddleware : IHttpMiddleware
{
    readonly Func<CancellationToken, Task<string?>> _clientToken;
    readonly string _language;
    public ClientTokenMiddleware(Func<CancellationToken, Task<string?>> clientToken) : this(clientToken, "en") { }
    public ClientTokenMiddleware(Func<CancellationToken, Task<string?>> clientToken, string language)
    { _clientToken = clientToken; _language = SpotifyHeaders.NormalizeLanguage(language); }

    public async Task<HttpResp> InvokeAsync(HttpReq req, Func<HttpReq, CancellationToken, Task<HttpResp>> next, CancellationToken ct)
    {
        var token = await _clientToken(ct).ConfigureAwait(false);
        var h = new Dictionary<string, string>(req.Headers)
        {
            ["App-Platform"] = SpotifyHeaders.AppPlatform,
            ["Spotify-App-Version"] = SpotifyHeaders.AppVersion,
            ["User-Agent"] = SpotifyHeaders.UserAgent,
            ["Accept-Language"] = _language,
        };
        if (!string.IsNullOrEmpty(token)) h["client-token"] = token;
        return await next(req with { Headers = h }, ct).ConfigureAwait(false);
    }
}

/// <summary>Attaches Pathfinder's client-token and platform-specific identity headers.</summary>
public sealed class PathfinderHeadersMiddleware : IHttpMiddleware
{
    public const string PlatformHeader = "X-Wavee-Pathfinder-Platform";
    public const string DesktopPlatform = "desktop";
    public const string WebPlayerPlatform = "webplayer";

    // Matches the captured 1.2.94.583 desktop client (omg.saz). The Spotify/<semver> token is what pins the UA to a
    // shipping build, so it is COMPOSED from SpotifyRuntimeIdentity's version pin rather than re-typed here — bumping the
    // pin in that one file now moves this UA with it, instead of leaving a stale semver that contradicts every other header.
    static readonly string DesktopUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.7680.179 Spotify/"
        + SpotifyRuntimeIdentity.Default.DesktopSemver + " Safari/537.36";
    const string WebPlayerUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36";

    readonly Func<CancellationToken, Task<string?>> _clientToken;
    readonly string _language;

    public PathfinderHeadersMiddleware(Func<CancellationToken, Task<string?>> clientToken) : this(clientToken, "en") { }
    public PathfinderHeadersMiddleware(Func<CancellationToken, Task<string?>> clientToken, string language)
    { _clientToken = clientToken; _language = SpotifyHeaders.NormalizeLanguage(language); }

    public async Task<HttpResp> InvokeAsync(HttpReq req, Func<HttpReq, CancellationToken, Task<HttpResp>> next, CancellationToken ct)
    {
        var h = new Dictionary<string, string>(req.Headers, StringComparer.OrdinalIgnoreCase);
        bool webPlayer = h.TryGetValue(PlatformHeader, out var platform)
            && string.Equals(platform, WebPlayerPlatform, StringComparison.OrdinalIgnoreCase);
        h.Remove(PlatformHeader);

        h["accept"] = "application/json";
        h["content-type"] = "application/json";
        // Architecture-derived, not hardcoded: Wavee ships arm64 and the captured client reports Win32_ARM64 there.
        // SpotifyRuntimeIdentity.AppPlatform already resolves this (env override → runtime pin → OSArchitecture).
        h["app-platform"] = webPlayer ? "WebPlayer" : SpotifyRuntimeIdentity.AppPlatform;
        h["user-agent"] = webPlayer ? WebPlayerUa : DesktopUa;
        // The captured desktop client sends a DIFFERENT spotify-app-version per operation on the SAME socket: the
        // dotted desktop semver for its own ops, and the web-player build token for the two operations it serves from
        // the web-player bundle (queryArtistOverview / getAlbum). Wavee sent none at all before this — matching the
        // capture means the header now rides every Pathfinder call, keyed by the platform the caller selected.
        h["spotify-app-version"] = webPlayer
            ? SpotifyRuntimeIdentity.WebPlayerAppVersion
            : SpotifyRuntimeIdentity.Default.DesktopSemver;
        h["accept-language"] = _language;
        if (await _clientToken(ct).ConfigureAwait(false) is { Length: > 0 } token)
            h["client-token"] = token;

        return await next(req with { Headers = h }, ct).ConfigureAwait(false);
    }
}

/// <summary>Test seam: scripts responses by request, counts calls.</summary>
public sealed class FakeExchange : IHttpExchange
{
    readonly Func<HttpReq, int, HttpResp> _responder;
    public int Calls { get; private set; }
    public FakeExchange(Func<HttpReq, int, HttpResp> responder) => _responder = responder;
    public Task<HttpResp> SendAsync(HttpReq req, CancellationToken ct) => Task.FromResult(_responder(req, ++Calls));
}

/// <summary>Resolves Spotify access-point hosts from apresolve JSON ({"accesspoint":["host:port", ...]}). Parse is testable;
/// the GET itself needs network.</summary>
public static class ApResolver
{
    public static IReadOnlyList<string> ParseAccessPoints(string json) => ParseHosts(json, "accesspoint");

    /// <summary>Parses a host list under <paramref name="key"/> ("accesspoint", "spclient", "dealer") from apresolve JSON.</summary>
    public static IReadOnlyList<string> ParseHosts(string json, string key)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<string>();
        if (doc.RootElement.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
                if (e.GetString() is { } s) list.Add(s);
        return list;
    }
}
