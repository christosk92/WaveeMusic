using Wavee.Core;

namespace Wavee.Backend.Spotify;

// ── §7 seam adapter — ISpotifySession over the native AuthFlow + ⑤ SessionContext + the premium gate ─────────────────
// ConnectAsync runs the credential-provider chain (stored → device-code → …), surfaces the device-code challenge as
// reactive AuthState, then (in the real impl) hands the credential to ③ Transport's AP channel, folds ProductInfo into ⑤
// SessionContext, and applies the premium gate. The AP socket/handshake is the remaining real-network step (needs creds +
// connectivity to E2E-verify); the credential acquisition + state + gating are real and unit-tested here.
public sealed class SpotifyAuthSession : ISpotifySession
{
    readonly AuthFlow _flow;
    readonly SessionContextHost _session;
    readonly SimpleSubject<AuthStatus> _status = new(AuthStatus.LoggedOut);
    AuthStatus _cur = AuthStatus.LoggedOut;

    public SpotifyAuthSession(AuthFlow flow, SessionContextHost session)
    {
        _flow = flow;
        _session = session;
    }

    public AuthStatus Status => _cur;
    public WaveeUser? CurrentUser { get; private set; }
    public IObservable<AuthStatus> StatusChanged => _status;

    /// <summary>The richer reactive auth state (challenge / QR / phase) the UI binds — beyond the coarse AuthStatus.</summary>
    public IObservable<AuthState> AuthState => _flow.State;

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        Set(AuthStatus.Authenticating);
        try
        {
            var cred = await _flow.AcquireAsync(ct).ConfigureAwait(false);
            if (cred is null) { Set(AuthStatus.Error); return false; }

            // (real: AP handshake with `cred` via ③ Transport → ProductInfo → fold tier/market into ⑤ SessionContext)
            _flow.Connecting();

            // ⑤ premium gate — Wavee refuses Free outright (Wavee.Backend.SessionGate).
            if (!SessionGate.IsAllowed(_session.Current.Tier))
            {
                _flow.Failed("Spotify Premium required");
                Set(AuthStatus.Error);
                return false;
            }

            CurrentUser = new WaveeUser(_session.Current.Account, "Me", null, IsPremium: true);
            _flow.Connected(CurrentUser);
            Set(AuthStatus.Authenticated);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Set(AuthStatus.LoggedOut);   // genuine cancel → back to logged-out
            throw;
        }
        catch (Exception ex)
        {
            // ANY failure (network timeout, rejection, …) resolves to Error — never leaves the UI pinned on Authenticating.
            _flow.Failed(ex.Message);
            Set(AuthStatus.Error);
            return false;
        }
    }

    public Task LogoutAsync(CancellationToken ct = default)
    {
        CurrentUser = null;
        Set(AuthStatus.LoggedOut);
        return Task.CompletedTask;
    }

    void Set(AuthStatus s) { _cur = s; _status.OnNext(s); }
}
