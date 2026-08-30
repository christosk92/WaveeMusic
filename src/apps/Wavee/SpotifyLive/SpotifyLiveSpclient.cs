using Wavee.Backend;
using Wavee.Backend.Spotify;
using Wavee.Core;

namespace Wavee.SpotifyLive;

// Shared live spclient bring-up: login (AP) -> client-token (attestation) -> login5 (spclient access token) -> resolve an
// spclient host -> the middleware HttpPipeline (bearer + client-token + 429-backoff) + a SessionContext. The metadata and
// library probes build on this. Needs creds + network — the USER runs the probes; only the wire shape is unverifiable here.
public sealed record LiveSpclient(HttpPipeline Pipeline, string BaseUrl, SessionContext Session, string Username, string AccessToken, string DeviceId, Credential ReusableCredential, Func<CancellationToken, Task<string>> TokenProvider, string? ClientToken = null, ApConnection? ApChannel = null, Wavee.Backend.Persistence.ICredentialStore? CredStore = null, Func<CancellationToken, Task<string>>? ForceTokenProvider = null);

public static class SpotifyLiveSpclient
{
    // Spotify's public desktop client id. Single owner: Wavee.Backend.Audio.SpotifyRuntimeIdentity.KeymasterClientId.
    const string ClientId = Wavee.Backend.Audio.SpotifyRuntimeIdentity.KeymasterClientId;

    public static async Task<LiveSpclient?> ConnectAsync(WaveeLogger log, CancellationToken ct, bool retainApChannel = false,
        bool allowDeviceCode = true, IObserver<AuthState>? authObserver = null, Action? onCredentialAcquired = null,
        bool allowBrowser = false, string language = "en")
    {
        language = SpotifyHeaders.NormalizeLanguage(language);
        // retainApChannel: keep the login AP socket alive as the ONE persistent channel (login + audio-key share it). The
        // probes/premium-gate leave it false (the socket is disposed after login). allowDeviceCode/allowBrowser/authObserver/
        // onCredentialAcquired thread the in-app login UI: silent-vs-interactive, the method (browser/device), the challenge, Finalizing.
        var login = await SpotifyLiveLogin.LoginAsync(log, ct, retainApChannel, allowDeviceCode, authObserver, onCredentialAcquired, allowBrowser).ConfigureAwait(false);
        if (login is null) return null;
        var welcome = login.Welcome;
        var deviceId = login.DeviceId;
        if (login.Channel is not null)
        {
            try { await login.Channel.PublishPreferredLocaleAsync(language, ct).ConfigureAwait(false); }
            catch (Exception ex) { log.Info("Preferred locale publish failed (HTTP locale remains active): " + ex.Message); }
        }

        // 1. client-token (attestation) — required on spclient.
        log.Info("Fetching client-token (attestation)...");
        var clientToken = await new ClientTokenClient(ClientId, deviceId).GetAsync(ct).ConfigureAwait(false);
        log.Info(clientToken is null ? "  client-token: NONE (spclient will likely 403)." : "  client-token obtained.");

        // 2. login5 -> the spclient access token (the device-code OAuth token is a Web-API audience, not spclient).
        log.Info("Minting an spclient access token via login5...");
        var login5 = new Login5Client(ClientId, deviceId);
        Login5Client.AccessToken access;
        try
        {
            access = await login5.GetAccessTokenAsync(welcome.Username, welcome.ReusableCredentials, clientToken, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { log.Info("login5 failed: " + ex.Message); login.Channel?.Dispose(); return null; }
        log.Info("  access token obtained (expires " + access.ExpiresAt.ToString("u") + ").");

        // Re-minting access-token provider: login5 tokens expire (~1h), and a long-lived dealer/AP session re-invokes the
        // provider on every reconnect — refresh just before expiry (or on a forced 401 retry) instead of a stale constant.
        var current = access;
        var tokenGate = new SemaphoreSlim(1, 1);
        async Task<string> Provider(bool force, CancellationToken c)
        {
            await tokenGate.WaitAsync(c).ConfigureAwait(false);
            try
            {
                if (force || DateTimeOffset.UtcNow >= current.ExpiresAt - TimeSpan.FromMinutes(2))
                    current = await login5.GetAccessTokenAsync(welcome.Username, welcome.ReusableCredentials, clientToken, c).ConfigureAwait(false);
                return current.Token;
            }
            finally { tokenGate.Release(); }
        }

        // 3. resolve an spclient host.
        var spJson = await SharedHttp.Client.GetStringAsync("https://apresolve.spotify.com/?type=spclient", ct).ConfigureAwait(false);
        var spHosts = ApResolver.ParseHosts(spJson, "spclient");
        if (spHosts.Count == 0) { log.Info("No spclient hosts returned."); login.Channel?.Dispose(); return null; }
        string baseUrl = "https://" + spHosts[0].Split(':')[0];
        log.Info("spclient: " + baseUrl);

        // 4. the middleware pipeline over the real exchange.
        string accessToken = access.Token;
        var pipeline = new HttpPipeline(
            new HttpClientExchange(),
            new AuthMiddleware((force, c) => Provider(force, c)),   // refreshes on expiry / forced 401-retry
            new ClientTokenMiddleware(_ => Task.FromResult(clientToken), language),
            new RateLimitMiddleware());

        var tier = welcome.Product?.IsPremium == true ? Tier.Premium : Tier.Free;
        var session = new SessionContext(welcome.Username, welcome.Country ?? "US",
            tier == Tier.Premium ? "premium" : "free", language, tier, false);
        var reusable = new Credential(CredentialKind.ReusableBlob, welcome.Username, System.Convert.ToBase64String(welcome.ReusableCredentials));
        return new LiveSpclient(pipeline, baseUrl, session, welcome.Username, accessToken, deviceId, reusable, c => Provider(false, c), clientToken, login.Channel, login.CredStore,
            ForceTokenProvider: c => Provider(true, c));   // G6 — the dealer force-mints after a failed wss handshake
    }
}
