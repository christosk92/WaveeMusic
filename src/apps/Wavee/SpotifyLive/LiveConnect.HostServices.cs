using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Modules;
using Wavee.Sdk;
using Wavee.Sdk.Protocol;

namespace Wavee.SpotifyLive;

// ── Part 7.3 — the host services the Spotify playback module needs ───────────────────────────────────────────────────
// When Spotify playback moves out of this repo (Part 7 / M1) the module owns the CDN, the key derivation and the
// decryption — but it never owns the SESSION. Login, the AP socket, the client-token and the login5 access token stay
// here, because Connect, search and the library need them too. So the module asks the host for exactly three things:
//
//   host/auth/token   {provider:"spotify", force}   → AuthToken(accessToken, expiresAtUnixMs)
//   host/auth/context {provider:"spotify"}          → AuthContext(deviceId, clientToken, spclientBaseUrl, session)
//   spotify/audioKey  {fileIdHex, trackGidHex}      → AudioKeyResult(key)  — the 0x0c/0x0d exchange on OUR AP socket
//
// All three are gated on the `auth.spotify` permission, declared in the module manifest as `permission:auth.spotify`
// (the one permission vocabulary ModuleHostServices already checks — there is no second schema). They are registered at
// GO-LIVE and unregistered at logout through the LiveSeams.ModuleHostServices ledger entry, so a signed-out app refuses
// them — -32601 ("the host does not offer this") on a connection that never had them, a typed NeedsAuth on one that did
// (see the session slot below) — instead of handing a module a dead session. The same named-offline-value rule every
// other seam follows.
//
// The session facts arrive through ISpotifyHostSession, so nothing here compiles against the login stack: the one
// production implementation (SpotifyLiveHostSession) is built by the go-live composition out of the live LiveSpclient
// handle and the AP socket, and the tests drive the very same registration over their own implementation.

/// <summary>The AP audio-key exchange, as a delegate: <c>ApConnection.RequestAudioKeyAsync</c> matches it verbatim, and
/// <see cref="SpotifyLiveHostSession.NoApChannel"/> is the named fail-loud stand-in for a session that has none.</summary>
/// <param name="fileId">The audio file id (20 bytes).</param>
/// <param name="trackGid">The track/episode gid (16 bytes).</param>
/// <param name="ct">Cancellation.</param>
public delegate Task<byte[]> SpotifyAudioKeyRequest(ReadOnlyMemory<byte> fileId, ReadOnlyMemory<byte> trackGid,
    CancellationToken ct);

/// <summary>The session facts the Spotify host services answer from.</summary>
public interface ISpotifyHostSession
{
    /// <summary>Mint (or re-use) the spclient bearer token.</summary>
    /// <param name="force">True to force a refresh — what a module does after a 401.</param>
    /// <param name="ct">Cancellation.</param>
    Task<string> GetAccessTokenAsync(bool force, CancellationToken ct);

    /// <summary>The non-secret session identity: device id, client token, spclient base url, account attributes.</summary>
    AuthContext GetAuthContext();

    /// <summary>Fetch the 16-byte AES audio key over the AP socket.</summary>
    /// <param name="fileId">The audio file id (20 bytes).</param>
    /// <param name="trackGid">The track/episode gid (16 bytes).</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="ModuleException">With <see cref="ModuleErrorCode.Unavailable"/> when there is no AP channel or
    /// the AP refuses the key.</exception>
    Task<byte[]> RequestAudioKeyAsync(ReadOnlyMemory<byte> fileId, ReadOnlyMemory<byte> trackGid, CancellationToken ct);
}

/// <summary>The live session behind the Spotify host services. Every dependency is REQUIRED: a session with no retained
/// AP socket passes <see cref="NoApChannel"/>, which refuses loudly, rather than a null that would answer an empty
/// key.</summary>
public sealed class SpotifyLiveHostSession : ISpotifyHostSession
{
    readonly Func<bool, CancellationToken, Task<string>> _accessToken;
    readonly Func<AuthContext> _authContext;
    readonly SpotifyAudioKeyRequest _audioKey;

    /// <summary>Build the session view.</summary>
    /// <param name="accessToken">Mints the spclient bearer token; the flag is the module's <c>force</c>, which the live
    /// session routes to <c>LiveSpclient.ForceTokenProvider</c>.</param>
    /// <param name="authContext">Reads the CURRENT session identity (it is re-read per call, so a market/tier change
    /// reaches the module without a re-registration).</param>
    /// <param name="audioKey">The AP exchange, or <see cref="NoApChannel"/>.</param>
    public SpotifyLiveHostSession(Func<bool, CancellationToken, Task<string>> accessToken,
        Func<AuthContext> authContext, SpotifyAudioKeyRequest audioKey)
    {
        ArgumentNullException.ThrowIfNull(accessToken);
        ArgumentNullException.ThrowIfNull(authContext);
        ArgumentNullException.ThrowIfNull(audioKey);
        _accessToken = accessToken;
        _authContext = authContext;
        _audioKey = audioKey;
    }

    /// <summary>Map the app's own session record onto the wire shape.</summary>
    /// <param name="deviceId">The app's device id.</param>
    /// <param name="clientToken">The attestation client-token, when one was obtained.</param>
    /// <param name="spclientBaseUrl">The resolved spclient host the app is using.</param>
    /// <param name="session">The signed-in account's attributes.</param>
    public static AuthContext ContextOf(string deviceId, string? clientToken, string? spclientBaseUrl,
        SessionContext session)
        => new(deviceId, clientToken, spclientBaseUrl,
            new AuthSession(session.Account, session.Market, session.Catalogue, session.Locale,
                session.Tier == Tier.Premium ? "premium" : "free", session.ExplicitFilter));

    /// <summary>The audio-key seam for a session whose login AP socket could not be retained: a typed refusal, so the
    /// module falls back to its own licence path instead of decrypting with nothing.</summary>
    /// <param name="fileId">Unused.</param>
    /// <param name="trackGid">Unused.</param>
    /// <param name="ct">Unused.</param>
    public static Task<byte[]> NoApChannel(ReadOnlyMemory<byte> fileId, ReadOnlyMemory<byte> trackGid,
        CancellationToken ct)
        => throw new ModuleException(ModuleErrorCode.Unavailable, "this session has no AP channel");

    /// <inheritdoc/>
    public Task<string> GetAccessTokenAsync(bool force, CancellationToken ct) => _accessToken(force, ct);

    /// <inheritdoc/>
    public AuthContext GetAuthContext() => _authContext();

    /// <inheritdoc/>
    public async Task<byte[]> RequestAudioKeyAsync(ReadOnlyMemory<byte> fileId, ReadOnlyMemory<byte> trackGid,
        CancellationToken ct)
    {
        try { return await _audioKey(fileId, trackGid, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ModuleException) { throw; }
        catch (Exception ex)
        {
            throw new ModuleException(ModuleErrorCode.Unavailable, "the AP refused the audio key: " + ex.Message);
        }
    }
}

public sealed partial class LiveConnect
{
    /// <summary>The provider key the Spotify host services answer for.</summary>
    public const string SpotifyProvider = "spotify";

    /// <summary>The permission a manifest must declare (as <c>permission:auth.spotify</c>) to call any of them.</summary>
    public const string SpotifyAuthPermission = "auth.spotify";

    /// <summary>How long an spclient access token is assumed to live. login5 mints ~1 h tokens and the app's own
    /// provider re-mints two minutes before expiry, but the provider seam hands back the token STRING only — there is
    /// no expiry on it to forward. So the host reports a deliberately conservative 55 minutes: a module that honours it
    /// re-asks slightly early (cheap — the provider just returns the cached token) instead of running a stale bearer
    /// into a 401.</summary>
    public static readonly TimeSpan AssumedTokenLifetime = TimeSpan.FromMinutes(55);

    /// <summary>The wire methods <see cref="RegisterModuleHostServices"/> installs (diagnostics + the uninstall).</summary>
    public static readonly string[] SpotifyHostServiceMethods =
        [ModuleMethods.AuthToken, ModuleMethods.AuthContext, ModuleMethods.AudioKey];

    // The signed-in session, behind ONE indirection the installed handlers read per call. Unregistering a method drops
    // it from the registry — which is what stops a NEW connection ever seeing it — but a module that is already up has
    // the handler installed on its own connection, and nothing can reach in and take it off. Without this slot, logout
    // would leave that live handler closed over a dead session: the exact silent-false-success shape wiring-discipline
    // forbids. Emptying the slot turns it into a typed "no signed-in Spotify session" instead. One slot, because the
    // app has at most one live Spotify session at a time (the same reason ModuleHost.Current is a single value).
    static volatile ISpotifyHostSession? _session;

    /// <summary>Install the three Spotify host services. Called once by the go-live composition through the
    /// <c>LiveSeams.ModuleHostServices</c> ledger entry, whose inverse is <see cref="UnregisterModuleHostServices"/>;
    /// the registry re-installs onto connections that are already up, so a module started before sign-in gets them too.</summary>
    /// <param name="services">The app-level host-service registry.</param>
    /// <param name="session">The session to answer from.</param>
    public static void RegisterModuleHostServices(ModuleHostServices services, ISpotifyHostSession session)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(session);
        _session = session;

        services.Register(ModuleMethods.AuthToken, SpotifyAuthPermission,
            SdkJsonContext.Default.AuthTokenParams, SdkJsonContext.Default.AuthToken,
            async (_, p, ct) =>
            {
                RequireSpotify(p.Provider);
                string token = await Session().GetAccessTokenAsync(p.Force, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(token))
                    throw new ModuleException(ModuleErrorCode.NeedsAuth, "the host holds no Spotify access token");
                return new AuthToken(token, ExpiresAtUnixMs(DateTimeOffset.UtcNow));
            });

        services.Register(ModuleMethods.AuthContext, SpotifyAuthPermission,
            SdkJsonContext.Default.AuthContextParams, SdkJsonContext.Default.AuthContext,
            (_, p, _) =>
            {
                RequireSpotify(p.Provider);
                return ValueTask.FromResult(Session().GetAuthContext());
            });

        services.Register(ModuleMethods.AudioKey, SpotifyAuthPermission,
            SdkJsonContext.Default.AudioKeyParams, SdkJsonContext.Default.AudioKeyResult,
            async (_, p, ct) =>
            {
                byte[] fileId = ParseHex(p.FileIdHex, "fileIdHex");
                byte[] trackGid = ParseHex(p.TrackGidHex, "trackGidHex");
                byte[] key = await Session().RequestAudioKeyAsync(fileId, trackGid, ct).ConfigureAwait(false);
                if (key is null || key.Length == 0)
                    throw new ModuleException(ModuleErrorCode.Unavailable, "the AP returned no audio key");
                return new AudioKeyResult(key);
            });
    }

    /// <summary>Drop the three Spotify host services — the go-live ledger's inverse. A module that calls one afterwards
    /// gets <c>-32601</c>, which the SDK reads as "the host does not offer this" rather than as a failure.</summary>
    /// <param name="services">The app-level host-service registry.</param>
    public static void UnregisterModuleHostServices(ModuleHostServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _session = null;
        foreach (string method in SpotifyHostServiceMethods) services.Unregister(method);
    }

    static ISpotifyHostSession Session()
        => _session ?? throw new ModuleException(ModuleErrorCode.NeedsAuth, "no signed-in Spotify session");

    /// <summary>The expiry the host reports for a token it only knows the string of (see
    /// <see cref="AssumedTokenLifetime"/>).</summary>
    /// <param name="now">The current time.</param>
    internal static long ExpiresAtUnixMs(DateTimeOffset now) => now.Add(AssumedTokenLifetime).ToUnixTimeMilliseconds();

    static void RequireSpotify(string? provider)
    {
        if (!string.Equals(provider, SpotifyProvider, StringComparison.OrdinalIgnoreCase))
            throw new ModuleException(ModuleErrorCode.Unsupported,
                "this host owns no provider '" + (provider ?? "") + "'");
    }

    static byte[] ParseHex(string? value, string name)
    {
        if (string.IsNullOrEmpty(value))
            throw new ModuleException(ModuleErrorCode.Unsupported, name + " is required");
        try { return Convert.FromHexString(value); }
        catch (FormatException) { throw new ModuleException(ModuleErrorCode.Unsupported, name + " is not hex"); }
    }
}
