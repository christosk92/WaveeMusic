using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Backend.Spotify;

// ── OAuth 2.0 Authorization Code + PKCE over a localhost loopback redirect ─────────────────────────────────────────────
// The librespot / Spotify-desktop "open the browser" pattern: open the system browser to accounts.spotify.com/authorize,
// capture the auth code on http://127.0.0.1:<port>/login, then exchange it (with the PKCE verifier) for an access token.
// Returns the SAME OAuthToken credential shape DeviceCodeProvider does, so it slots into AuthFlow as a peer interactive
// provider (no downstream change — the AP login consumes the token identically).
//
// HONESTY NOTE: this works only if the loopback redirect is registered for the client_id. That is UNVERIFIED for the
// hardcoded desktop client (65b708073fc0480ea92a077233ca87bd) — device-code is the confirmed path; this is the user-chosen
// "browser" alternative. If the authorize step rejects the redirect_uri, AcquireAsync returns null and the flow falls back.
public sealed class LoopbackOAuthProvider : ICredentialProvider
{
    const string AuthorizeEndpoint = "https://accounts.spotify.com/authorize";
    const string TokenEndpoint = "https://accounts.spotify.com/api/token";

    readonly IHttpPost _http;
    readonly string _clientId;
    readonly string[] _scopes;
    readonly Action<string> _openBrowser;

    public LoopbackOAuthProvider(IHttpPost http, string clientId, string[] scopes, Action<string>? openBrowser = null)
    {
        _http = http; _clientId = clientId; _scopes = scopes;
        _openBrowser = openBrowser ?? OpenSystemBrowser;
    }

    public string Name => "browser-pkce";

    public async Task<Credential?> AcquireAsync(IAuthSink sink, CancellationToken ct)
    {
        // PKCE: a high-entropy verifier + its S256 challenge, and an anti-CSRF state nonce.
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));

        // A loopback listener on an OS-assigned free port (no inbound firewall rule needed for 127.0.0.1).
        int port = FreePort();
        string redirect = "http://127.0.0.1:" + port + "/login";
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
        try { listener.Start(); }
        catch { return null; }   // listener couldn't bind → let the flow fall through

        string url = AuthorizeEndpoint
            + "?client_id=" + Uri.EscapeDataString(_clientId)
            + "&response_type=code"
            + "&redirect_uri=" + Uri.EscapeDataString(redirect)
            + "&code_challenge_method=S256&code_challenge=" + challenge
            + "&state=" + state
            + "&scope=" + Uri.EscapeDataString(string.Join(" ", _scopes));
        _openBrowser(url);

        // Await the browser redirect; a cancel (user backed out / superseded) stops the listener and unblocks the wait.
        HttpListenerContext ctx;
        using (ct.Register(() => { try { listener.Stop(); } catch { } }))
        {
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch { return null; }
        }

        var q = ctx.Request.QueryString;
        string? code = q["code"], gotState = q["state"], err = q["error"];
        Respond(ctx, err is null && code is not null);

        if (err is not null || code is null || gotState != state) return null;   // denied / mismatch → fall through

        // Exchange the auth code (+ the PKCE verifier) for tokens — through the same IHttpPost seam the device flow uses.
        var r = await _http.PostFormAsync(TokenEndpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirect,
            ["client_id"] = _clientId,
            ["code_verifier"] = verifier,
        }, ct).ConfigureAwait(false);
        if (r.Status != 200) return null;

        try
        {
            using var doc = JsonDocument.Parse(r.Body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("access_token", out var at) || at.GetString() is not { Length: > 0 } access) return null;
            string? refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
            int ein = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
            return new Credential(CredentialKind.OAuthToken, "", access, DateTimeOffset.UtcNow.AddSeconds(ein), refresh);
        }
        catch (JsonException) { return null; }
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static void Respond(HttpListenerContext ctx, bool ok)
    {
        string html = "<!doctype html><meta charset=utf-8><title>Wavee</title>"
            + "<body style=\"font:16px -apple-system,Segoe UI,system-ui;text-align:center;padding:56px;background:#0b0b0c;color:#f5f5f4\">"
            + (ok ? "<h2>You're signed in to Wavee.</h2><p style='color:#a8a29e'>You can close this tab and return to the app.</p>"
                  : "<h2>Sign-in was cancelled.</h2><p style='color:#a8a29e'>You can close this tab and try again in the app.</p>")
            + "</body>";
        try
        {
            var bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }
        catch { }
    }

    static void OpenSystemBrowser(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}
