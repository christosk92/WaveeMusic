using System.Linq;
using Wavee.Backend;
using Wavee.Backend.Persistence;
using Wavee.Backend.Spotify;

namespace Wavee.SpotifyLive;

// End-to-end LIVE login with persisted credentials.
// Logging discipline: plain ASCII only; the access token and the reusable-credential BYTES are NEVER logged (only the byte
// count); the account username is REDACTED. The device user-code is surfaced only for the interactive CLI prompt (it is
// transient + single-use and the user must enter it). In-app, the challenge surfaces via AuthState in the UI, not the log.
public static class SpotifyLiveLogin
{
    // Spotify's public desktop client id. Single owner: Wavee.Backend.Audio.SpotifyRuntimeIdentity.KeymasterClientId.
    const string ClientId = Wavee.Backend.Audio.SpotifyRuntimeIdentity.KeymasterClientId;
    static readonly string[] Scopes =
    [
        "streaming", "app-remote-control", "user-read-playback-state", "user-modify-playback-state",
        "user-read-currently-playing", "playlist-read-private", "user-library-read", "user-read-email", "user-read-private",
    ];

    /// <summary>The shared result of a live login — enough to continue to login5 / spclient.</summary>
    public sealed record LoginResult(SpotifyWelcome Welcome, string DeviceId, LocalCredentialStore CredStore, string Scheme,
        ApConnection? Channel = null);

    public static async Task<int> RunAsync(WaveeLogger log, CancellationToken ct)
    {
        var login = await LoginAsync(log, ct).ConfigureAwait(false);
        if (login is null) return 1;
        var w = login.Welcome;

        // Step 2 - the REAL tier from ProductInfo drives the premium gate. Only a CONFIRMED premium product passes;
        // null/unknown (the trailing 0x50 missing) is treated as Free and refused, so a missing packet can't slip a
        // non-premium account through. (The 0x50 reliably arrives within the post-welcome window; absence is anomalous.)
        var tier = w.Product?.IsPremium == true ? Tier.Premium : Tier.Free;
        if (!SessionGate.IsAllowed(tier))
        {
            log.Info(SessionGate.WarningTitle + " - the signed-in account is not Premium. Refusing (no launch).");
            return 2;
        }
        log.Info("Logged in [" + login.Scheme + "]; next launch skips device-code.");
        return 0;
    }

    /// <summary>The credential chain (stored reusable creds → device-code) → AP handshake/login (with AP failover) → APWelcome,
    /// persisting the fresh reusable credentials. Returns null on any failure (already logged). Reused by RunAsync (the
    /// premium gate) and by the metadata probe (which continues to login5 + spclient).</summary>
    public static async Task<LoginResult?> LoginAsync(WaveeLogger log, CancellationToken ct, bool retainChannel = false,
        bool allowDeviceCode = true, IObserver<AuthState>? authObserver = null, Action? onCredentialAcquired = null,
        bool allowBrowser = false)
    {
        // Step 1 - PORTABLE credential store (DPAPI / Keychain / libsecret / NoOp behind one seam) + the launch-stable device id.
        var (credStore, deviceId) = OpenCredentialStore();

        try
        {
            // Stored creds first (silent fast-path), then the chosen interactive method: browser-loopback (PKCE) and/or the
            // device code. A SILENT resume passes neither → [stored] only.
            var providers = new List<ICredentialProvider> { new StoredCredentialProvider(() => credStore.Load()) };
            if (allowBrowser) providers.Add(new LoopbackOAuthProvider(new HttpClientPost(), ClientId, Scopes));
            if (allowDeviceCode) providers.Add(new DeviceCodeProvider(new HttpClientPost(), ClientId, Scopes));
            var flow = new AuthFlow(providers);
            using var challengeSub = flow.State.Subscribe(new ChallengeLogger(log));
            // The in-app UI observer rides the SAME reactive state (device-code challenge / QR / expiry); null for the CLI.
            using var uiSub = authObserver is null ? (IDisposable?)null : flow.State.Subscribe(authObserver);
            log.Info("Authenticating (stored credentials first" + (allowDeviceCode ? ", else device-code)..." : ", silent)..."));
            var cred = await flow.AcquireAsync(ct).ConfigureAwait(false);
            if (cred is null) { log.Info("No credential obtained."); return null; }
            onCredentialAcquired?.Invoke();   // credential-acquired boundary → the bootstrap reports Finalizing (AP/login5/dealer ahead)
            bool usedStored = cred.Kind == CredentialKind.ReusableBlob;
            log.Info(usedStored ? "Using stored credentials (no re-auth)." : "Authorized via device-code.");

            log.Info("Resolving Spotify access points...");
            var json = await SharedHttp.Client.GetStringAsync("https://apresolve.spotify.com/?type=accesspoint", ct).ConfigureAwait(false);
            var aps = ApResolver.ParseAccessPoints(json);
            if (aps.Count == 0) { log.Info("No access points returned."); return null; }
            // :4070 endpoints first, then the rest — FAIL OVER to the next AP on a connection error / timeout / TryAnotherAP.
            var ordered = aps.Where(a => a.EndsWith(":4070")).Concat(aps.Where(a => !a.EndsWith(":4070"))).ToList();

            SpotifyWelcome? welcome = null;
            ApConnection? channel = null;
            foreach (var apHostPort in ordered)
            {
                var parts = apHostPort.Split(':');
                string host = parts[0];
                int port = int.Parse(parts[1]);
                log.Info("Connecting to " + host + ":" + port + "...");
                TcpDuplexStream? tcp = null;
                bool keep = false;
                try
                {
                    // retainChannel: keep THIS socket alive as the one persistent AP channel (login + audio-key share it —
                    // no second handshake). Otherwise the socket is disposed after login (the original probe/gate behavior).
                    tcp = await TcpDuplexStream.ConnectAsync(host, port, ct, retainChannel ? ApConnection.IdleReadTimeout : null).ConfigureAwait(false);
                    if (retainChannel)
                    {
                        var (w, codec) = await SpotifyConnection.HandshakeRetainAsync(tcp, cred, deviceId, ct).ConfigureAwait(false);
                        welcome = w;
                        channel = ApConnection.Adopt(tcp, codec, log);   // the login socket becomes the persistent AP channel
                        keep = true;
                    }
                    else
                    {
                        welcome = await SpotifyConnection.HandshakeAndLoginAsync(tcp, cred, deviceId, ct).ConfigureAwait(false);
                    }
                    break;   // logged in
                }
                catch (SpotifyAuthRejectedException ex)
                {
                    // A genuine credential rejection is FINAL (don't try other APs). Clear stored creds → a re-run re-auths.
                    if (usedStored) { credStore.Clear(); log.Info("Stored credentials were rejected - cleared them. Re-run to authorize fresh."); }
                    else log.Info("Login rejected by Spotify: " + ex.Message);
                    return null;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }   // user cancel → stop
                catch (Exception ex) { log.Info("  " + host + " failed (" + ex.Message + ") - trying the next access point."); }   // TryAnotherAP / connection / timeout
                finally { if (!keep) tcp?.Dispose(); }   // dispose on failure / non-retain (matches the old `using`)
            }
            if (welcome is null) { log.Info("All access points failed."); return null; }

            log.Info("Logged in to Spotify (user " + Redact(welcome.Username) + ", product " + (welcome.Product?.Type ?? "unknown") + ", country " + (welcome.Country ?? "?") + ").");

            // Persist the fresh reusable credentials (bytes never logged, only the count) — login5 + the next launch use them.
            credStore.Save(new Credential(CredentialKind.ReusableBlob, welcome.Username, Convert.ToBase64String(welcome.ReusableCredentials)));
            log.Info("Saved " + welcome.ReusableCredentials.Length + "-byte reusable credentials [" + credStore.Scheme + "].");
            return new LoginResult(welcome, deviceId, credStore, credStore.Scheme, channel);
        }
        catch (OperationCanceledException) { log.Info("Live login timed out / cancelled."); return null; }
        catch (Exception ex) { log.Info("Live login failed: " + ex.Message); return null; }
    }

    static string GetOrCreateDeviceId(ILocalStore store)
    {
        var id = store.Get("device.id");
        if (string.IsNullOrEmpty(id)) { id = Guid.NewGuid().ToString("N"); store.Set("device.id", id); }
        return id;
    }

    /// <summary>Open the portable credential store (DPAPI on Windows, Keychain/libsecret on macOS/Linux, else NoOp — the
    /// same seam) together with the persisted, launch-stable device id. ONE source of truth for the protector selection,
    /// shared by <see cref="LoginAsync"/> and the silent-resume pre-check.</summary>
    public static (LocalCredentialStore Store, string DeviceId) OpenCredentialStore()
    {
        ICredentialProtector protector = new NoOpProtector();
        if (OperatingSystem.IsWindows()) protector = new DpapiProtector();
        else if ((OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) && KeyringProtector.IsAvailable()) protector = new KeyringProtector();
        var localStore = FileLocalStore.ForApp("Wavee");
        return (new LocalCredentialStore(localStore, protector), GetOrCreateDeviceId(localStore));
    }

    /// <summary>True iff a reusable credential for THIS machine/platform is on disk (scheme-matched). The takeover's silent
    /// resume uses it to resolve "no stored credential" to Welcome instead of the Error card.</summary>
    public static bool HasStoredCredential()
    {
        try { return OpenCredentialStore().Store.Load() is not null; }
        catch { return false; }
    }

    /// <summary>Wipe the persisted reusable credential (logout). Opens the store FRESH so it works even when no live session
    /// captured one, and persists the deletion to disk immediately (so the next login can't silently re-use it).</summary>
    public static void ClearStoredCredential()
    {
        try { OpenCredentialStore().Store.Clear(); }
        catch { }
    }

    // Redact an account identifier before it reaches a log (keep a short hint, hide the rest).
    static string Redact(string s) => string.IsNullOrEmpty(s) ? "(none)" : s.Length <= 6 ? "***" : s[..3] + "***" + s[^2..];

    sealed class ChallengeLogger(WaveeLogger log) : IObserver<AuthState>
    {
        bool _shown;
        public void OnCompleted() { }
        public void OnError(Exception e) { }
        public void OnNext(AuthState s)
        {
            if (s.Phase == AuthPhase.ChallengeExpired) { log.Info("The device code expired before approval. Re-run to get a fresh code."); return; }
            if (_shown || s.Phase != AuthPhase.AwaitingUser || s.Challenge is not { } c) return;
            _shown = true;
            log.Info("Authorize Wavee on Spotify (Premium account):");
            log.Info("  Open: " + (c.VerificationUriComplete ?? c.VerificationUri));
            log.Info("  or visit " + c.VerificationUri + " and enter code: " + c.UserCode);
            log.Info("Waiting for approval...");
        }
    }
}
