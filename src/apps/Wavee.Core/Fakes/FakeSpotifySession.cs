namespace Wavee.Core;

/// <summary>In-process fake session: connects instantly to a fake premium user. No network, no auth.</summary>
public sealed class FakeSpotifySession : ISpotifySession, ISessionSource
{
    readonly SimpleSubject<AuthStatus> _status = new(AuthStatus.LoggedOut);

    public AuthStatus Status { get; private set; } = AuthStatus.LoggedOut;
    public WaveeUser? CurrentUser { get; private set; }
    public IObservable<AuthStatus> StatusChanged => _status;

    // ── ISource: the Session facet, declared for the federation registry (docs/plans/wavee/architecture.md §4.2). ──
    public string Id => "local-session";
    public bool Owns(string uri) => false;
    public SourceCapabilities Capabilities => SourceCapabilities.Session;

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        Set(AuthStatus.Authenticating);
        await Task.Delay(250, ct).ConfigureAwait(false);
        CurrentUser = new WaveeUser("u_wavee", "Wavee Listener", null, IsPremium: true, Email: "listener@wavee.app");
        Set(AuthStatus.Authenticated);
        return true;
    }

    public Task LogoutAsync(CancellationToken ct = default)
    {
        CurrentUser = null;
        Set(AuthStatus.LoggedOut);
        return Task.CompletedTask;
    }

    void Set(AuthStatus s) { Status = s; _status.OnNext(s); }
}
