using System.Net.Http;
using System.Text.Json;
using Wavee.Core;

namespace Wavee.Backend.Spotify;

// ── NATIVE auth architecture (NOT a 1:1 port of OAuthClient/DeviceCodeFlow/Authenticator/CredentialsCache) ───────────
// What auth does: obtain a CREDENTIAL → expose reactive STATE → hand the credential to ③ Transport's AP channel.
// Native shape, in line with the engines already built:
//   • credential acquisition is a STRATEGY SET (ICredentialProvider, tried in order) — like Mutation/Resource strategies;
//   • the interactive flow is reactive STATE (AuthState SimpleSubject + an AuthChallenge), not events — so the device-code
//     UI is a projection; • HTTP is behind a seam (IHttpPost) so the whole flow is deterministically unit-testable.

public enum CredentialKind { OAuthToken, ReusableBlob }
public sealed record Credential(CredentialKind Kind, string Username, string Secret, DateTimeOffset? Expiry = null, string? Refresh = null);

public readonly record struct HttpResult(int Status, string Body);

/// <summary>The single HTTP seam the OAuth providers post through — real impl uses HttpClient; tests use a fake.</summary>
public interface IHttpPost
{
    Task<HttpResult> PostFormAsync(string url, IReadOnlyDictionary<string, string> form, CancellationToken ct);
}

/// <summary>The device-code interactive surface, raised as reactive state (user_code / verification_uri / QR).</summary>
public sealed record AuthChallenge(string UserCode, string VerificationUri, string? VerificationUriComplete, DateTimeOffset Expiry);

public enum AuthPhase { LoggedOut, AwaitingCredential, AwaitingUser, ChallengeExpired, Connecting, Connected, Failed }
public sealed record AuthState(AuthPhase Phase, AuthChallenge? Challenge = null, string? Error = null, WaveeUser? User = null);

/// <summary>How a provider surfaces an interactive challenge back to the flow (a sink, so providers stay UI-agnostic).</summary>
public interface IAuthSink
{
    void Challenge(AuthChallenge challenge);
    void Expired();   // the interactive challenge (device code / QR) lapsed before approval — UI shows "expired, regenerate"
}

/// <summary>A credential-acquisition strategy. Returns a credential, or null to fall through to the next provider.</summary>
public interface ICredentialProvider
{
    string Name { get; }
    Task<Credential?> AcquireAsync(IAuthSink sink, CancellationToken ct);
}

/// <summary>Drives the providers in order and publishes reactive AuthState (incl. the device-code challenge).</summary>
public sealed class AuthFlow : IAuthSink
{
    readonly IReadOnlyList<ICredentialProvider> _providers;
    readonly SimpleSubject<AuthState> _state = new(new AuthState(AuthPhase.LoggedOut));

    public AuthFlow(IEnumerable<ICredentialProvider> providers) => _providers = providers.ToList();

    public IObservable<AuthState> State => _state;
    public AuthState Current { get; private set; } = new(AuthPhase.LoggedOut);

    void Set(AuthState s) { Current = s; _state.OnNext(s); }
    public void Challenge(AuthChallenge c) => Set(new AuthState(AuthPhase.AwaitingUser, Challenge: c));
    public void Expired() => Set(new AuthState(AuthPhase.ChallengeExpired));   // QR/code lapsed → UI offers "regenerate"
    public void Connecting() => Set(new AuthState(AuthPhase.Connecting));
    public void Connected(WaveeUser user) => Set(new AuthState(AuthPhase.Connected, User: user));
    public void Failed(string err) => Set(new AuthState(AuthPhase.Failed, Error: err));

    /// <summary>Try each provider in order; first non-null credential wins. Stored-first, then interactive.</summary>
    public async Task<Credential?> AcquireAsync(CancellationToken ct)
    {
        Set(new AuthState(AuthPhase.AwaitingCredential));
        string? lastError = null;
        foreach (var p in _providers)
        {
            try
            {
                var cred = await p.AcquireAsync(this, ct).ConfigureAwait(false);
                if (cred != null) return cred;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }   // genuine user cancel
            catch (Exception ex) { lastError = ex.Message; }   // a network/timeout OCE (not from ct) or any error → try the next provider
        }
        // Don't clobber an expired-challenge state (the UI shows "regenerate") with a generic failure.
        if (Current.Phase != AuthPhase.ChallengeExpired) Failed(lastError ?? "no credential provider succeeded");
        return null;
    }
}

/// <summary>Stored reusable credential (the bootstrap-free fast path). Returns null when none is on disk → fall through.</summary>
public sealed class StoredCredentialProvider : ICredentialProvider
{
    readonly Func<Credential?> _load;
    public StoredCredentialProvider(Func<Credential?> load) => _load = load;
    public string Name => "stored";
    public Task<Credential?> AcquireAsync(IAuthSink sink, CancellationToken ct) => Task.FromResult(_load());
}

/// <summary>OAuth 2.0 Device Authorization Grant (RFC 8628). The poll state machine is the testable core; HTTP + delay are
/// injected seams so tests drive pending/slow_down/expired/denied/success deterministically without a clock or network.</summary>
public sealed class DeviceCodeProvider : ICredentialProvider
{
    const string DeviceAuthEndpoint = "https://accounts.spotify.com/oauth2/device/authorize";
    const string TokenEndpoint = "https://accounts.spotify.com/api/token";

    readonly IHttpPost _http;
    readonly string _clientId;
    readonly string[] _scopes;
    readonly Func<TimeSpan, CancellationToken, Task> _delay;
    readonly Func<DateTimeOffset> _now;

    public DeviceCodeProvider(IHttpPost http, string clientId, string[] scopes,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTimeOffset>? now = null)
    {
        _http = http; _clientId = clientId; _scopes = scopes;
        _delay = delay ?? ((d, c) => Task.Delay(d, c));
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public string Name => "device-code";

    public async Task<Credential?> AcquireAsync(IAuthSink sink, CancellationToken ct)
    {
        // 1. request device code
        var r = await _http.PostFormAsync(DeviceAuthEndpoint, new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["scope"] = string.Join(" ", _scopes),
        }, ct).ConfigureAwait(false);
        if (r.Status != 200) throw new InvalidOperationException($"device/authorize failed ({r.Status})");

        using var doc = JsonDocument.Parse(r.Body);
        var root = doc.RootElement;
        string deviceCode = root.GetProperty("device_code").GetString()!;
        string userCode = root.GetProperty("user_code").GetString()!;
        string verifyUri = root.GetProperty("verification_uri").GetString()!;
        string? verifyUriComplete = root.TryGetProperty("verification_uri_complete", out var vc) ? vc.GetString() : null;
        int expiresIn = root.GetProperty("expires_in").GetInt32();
        int interval = root.GetProperty("interval").GetInt32();

        var deadline = _now().AddSeconds(expiresIn);
        sink.Challenge(new AuthChallenge(userCode, verifyUri, verifyUriComplete, deadline));   // ← reactive state for the UI

        // 2. poll — RESILIENT (RFC 8628): a transient failure (network error, non-JSON body from a 502/proxy, an unknown
        //    error, 5xx) keeps polling until the deadline. Only expired_token / access_denied (and the deadline / a real
        //    cancel) are terminal — so a single blip can't kill a multi-minute login.
        var pollInterval = TimeSpan.FromSeconds(interval);
        await _delay(pollInterval, ct).ConfigureAwait(false);
        while (_now() < deadline)
        {
            ct.ThrowIfCancellationRequested();

            HttpResult pr;
            try
            {
                pr = await _http.PostFormAsync(TokenEndpoint, new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["device_code"] = deviceCode,
                    ["client_id"] = _clientId,
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { await _delay(pollInterval, ct).ConfigureAwait(false); continue; }   // transient network error → keep polling

            string? access = null, refresh = null, err = null;
            int ein = 3600;
            try
            {
                using var pd = JsonDocument.Parse(pr.Body);
                var pe = pd.RootElement;
                if (pr.Status == 200 && pe.TryGetProperty("access_token", out var at))
                {
                    access = at.GetString();
                    refresh = pe.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
                    ein = pe.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
                }
                else err = pe.TryGetProperty("error", out var ee) ? ee.GetString() : null;
            }
            catch (JsonException) { await _delay(pollInterval, ct).ConfigureAwait(false); continue; }   // 502 / HTML / empty body → keep polling

            if (access is not null) return new Credential(CredentialKind.OAuthToken, "", access, _now().AddSeconds(ein), refresh);

            switch (err)
            {
                case "expired_token": sink.Expired(); return null;   // the code lapsed → signal "expired, regenerate" (not an error)
                case "access_denied": throw new InvalidOperationException("user denied authorization");
                case "slow_down": pollInterval += TimeSpan.FromSeconds(5); await _delay(pollInterval, ct).ConfigureAwait(false); break;
                default: await _delay(pollInterval, ct).ConfigureAwait(false); break;   // authorization_pending / unknown / 5xx → keep polling
            }
        }
        sink.Expired();   // polling deadline reached without approval → same "expired, regenerate" state
        return null;
    }
}

/// <summary>One shared HttpClient over a SocketsHttpHandler with a bounded PooledConnectionLifetime — avoiding BOTH
/// anti-patterns: per-request `new HttpClient()` (socket exhaustion) AND a plain static HttpClient (stale DNS — a single
/// handler otherwise pins connections to their resolved IPs forever). Recycling the pool every couple of minutes
/// re-resolves DNS (server failover / load-balancer changes) while still reusing connections. This is what
/// IHttpClientFactory does internally, minus the DI dependency — right for this lightweight, portable backend.</summary>
public static class SharedHttp
{
    public static HttpClient Client => HttpPools.Get(HttpPool.ControlPlane);
}

/// <summary>Real IHttpPost over the shared HttpClient (the production seam). Tests substitute a fake (no socket).</summary>
public sealed class HttpClientPost : IHttpPost
{
    public async Task<HttpResult> PostFormAsync(string url, IReadOnlyDictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var resp = await SharedHttp.Client.PostAsync(url, content, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new HttpResult((int)resp.StatusCode, body);
    }
}

/// <summary>Test seam: scripts HTTP responses by (url, form). No network, no clock.</summary>
public sealed class FakeHttpPost : IHttpPost
{
    readonly Func<string, IReadOnlyDictionary<string, string>, HttpResult> _responder;
    public int Calls { get; private set; }
    public FakeHttpPost(Func<string, IReadOnlyDictionary<string, string>, HttpResult> responder) => _responder = responder;
    public Task<HttpResult> PostFormAsync(string url, IReadOnlyDictionary<string, string> form, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(_responder(url, form));
    }
}
