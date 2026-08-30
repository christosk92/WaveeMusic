using Wavee.Backend;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The native auth architecture: credential-provider strategies + reactive AuthState + the device-code state machine.
// All driven through the IHttpPost seam with a no-op delay and a frozen clock — deterministic, no network.
public class DeviceCodeFlowTests
{
    const string DeviceJson =
        """{"device_code":"DC","user_code":"ABCD","verification_uri":"https://spotify.com/pair","verification_uri_complete":"https://spotify.com/pair?code=ABCD","expires_in":600,"interval":5}""";
    const string SuccessJson =
        """{"access_token":"AT","refresh_token":"RT","expires_in":3600,"token_type":"Bearer"}""";

    static DeviceCodeProvider Provider(IHttpPost http, List<TimeSpan>? delays = null) =>
        new(http, "client-id", ["streaming"],
            delay: (d, c) => { delays?.Add(d); return Task.CompletedTask; },
            now: () => DateTimeOffset.UnixEpoch);   // frozen clock → never past the deadline; loop ends on success/error

    sealed class CaptureSink : IAuthSink
    {
        public AuthChallenge? Captured;
        public bool ExpiredSignaled;
        public void Challenge(AuthChallenge c) => Captured = c;
        public void Expired() => ExpiredSignaled = true;
    }

    [Fact]
    public async Task PendingThenSuccess_ReturnsCredential_AndRaisesChallenge()
    {
        int token = 0;
        var http = new FakeHttpPost((url, _) =>
            url.Contains("device/authorize")
                ? new HttpResult(200, DeviceJson)
                : (++token == 1 ? new HttpResult(400, """{"error":"authorization_pending"}""")
                                : new HttpResult(200, SuccessJson)));

        var sink = new CaptureSink();
        var cred = await Provider(http).AcquireAsync(sink, default);

        Assert.NotNull(cred);
        Assert.Equal(CredentialKind.OAuthToken, cred!.Kind);
        Assert.Equal("AT", cred.Secret);
        Assert.Equal("RT", cred.Refresh);
        Assert.NotNull(sink.Captured);
        Assert.Equal("ABCD", sink.Captured!.UserCode);
        Assert.Equal("https://spotify.com/pair?code=ABCD", sink.Captured.VerificationUriComplete);
        Assert.True(token >= 2);
    }

    [Fact]
    public async Task SlowDown_IncreasesPollInterval_ThenSucceeds()
    {
        int token = 0;
        var http = new FakeHttpPost((url, _) =>
            url.Contains("device/authorize")
                ? new HttpResult(200, DeviceJson)
                : (++token == 1 ? new HttpResult(400, """{"error":"slow_down"}""")
                                : new HttpResult(200, SuccessJson)));

        var delays = new List<TimeSpan>();
        var cred = await Provider(http, delays).AcquireAsync(new CaptureSink(), default);

        Assert.NotNull(cred);
        Assert.True(delays.Count >= 2);
        Assert.True(delays[1] > delays[0], "interval should grow after slow_down");
    }

    [Fact]
    public async Task ExpiredToken_SignalsExpired_ReturnsNull()
    {
        var http = new FakeHttpPost((url, _) =>
            url.Contains("device/authorize")
                ? new HttpResult(200, DeviceJson)
                : new HttpResult(400, """{"error":"expired_token"}"""));
        var sink = new CaptureSink();
        var cred = await Provider(http).AcquireAsync(sink, default);
        Assert.Null(cred);                  // no credential — the code lapsed
        Assert.True(sink.ExpiredSignaled);  // the UI is told to show "expired, regenerate"
    }

    [Fact]
    public async Task AccessDenied_Throws()
    {
        var http = new FakeHttpPost((url, _) =>
            url.Contains("device/authorize")
                ? new HttpResult(200, DeviceJson)
                : new HttpResult(400, """{"error":"access_denied"}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(http).AcquireAsync(new CaptureSink(), default));
    }

    [Fact]
    public async Task TransientBlip_KeepsPolling_ThenSucceeds()
    {
        int token = 0;
        var http = new FakeHttpPost((url, _) =>
            url.Contains("device/authorize")
                ? new HttpResult(200, DeviceJson)
                : (++token == 1 ? new HttpResult(502, "<html>Bad Gateway</html>")   // non-JSON proxy error → must NOT abort
                                : new HttpResult(200, SuccessJson)));
        var cred = await Provider(http).AcquireAsync(new CaptureSink(), default);
        Assert.NotNull(cred);
        Assert.Equal("AT", cred!.Secret);
        Assert.True(token >= 2, "kept polling past the 502 blip");
    }

    [Fact]
    public async Task UnknownError_KeepsPolling_ThenSucceeds()
    {
        int token = 0;
        var http = new FakeHttpPost((url, _) =>
            url.Contains("device/authorize")
                ? new HttpResult(200, DeviceJson)
                : (++token == 1 ? new HttpResult(500, """{"error":"server_error"}""")   // unknown/5xx → keep polling
                                : new HttpResult(200, SuccessJson)));
        var cred = await Provider(http).AcquireAsync(new CaptureSink(), default);
        Assert.NotNull(cred);
        Assert.True(token >= 2);
    }
}

public class AuthFlowTests
{
    static Credential Blob => new(CredentialKind.ReusableBlob, "user", "blob");

    [Fact]
    public async Task StoredCredential_WinsFirst_DeviceCodeNotTried()
    {
        var deviceHttp = new FakeHttpPost((_, _) => throw new Exception("device-code must not be reached"));
        var device = new DeviceCodeProvider(deviceHttp, "c", ["s"], delay: (_, _) => Task.CompletedTask);
        var flow = new AuthFlow([new StoredCredentialProvider(() => Blob), device]);

        var cred = await flow.AcquireAsync(default);

        Assert.Equal("blob", cred!.Secret);
        Assert.Equal(0, deviceHttp.Calls);
    }

    [Fact]
    public async Task NoStored_FallsThroughToDeviceCode_AndPublishesChallengeState()
    {
        const string deviceJson =
            """{"device_code":"DC","user_code":"WXYZ","verification_uri":"https://spotify.com/pair","expires_in":600,"interval":5}""";
        var http = new FakeHttpPost((url, _) =>
            url.Contains("device/authorize")
                ? new HttpResult(200, deviceJson)
                : new HttpResult(200, """{"access_token":"AT","expires_in":3600}"""));
        var device = new DeviceCodeProvider(http, "c", ["s"], delay: (_, _) => Task.CompletedTask, now: () => DateTimeOffset.UnixEpoch);

        var states = new List<AuthState>();
        var flow = new AuthFlow([new StoredCredentialProvider(() => null), device]);
        using var _ = flow.State.Subscribe(new Sink(states.Add));

        var cred = await flow.AcquireAsync(default);

        Assert.Equal("AT", cred!.Secret);
        Assert.Contains(states, s => s.Phase == AuthPhase.AwaitingUser && s.Challenge?.UserCode == "WXYZ");
    }

    [Fact]
    public async Task DeviceCodeExpiry_SurfacesChallengeExpired_NotFailed()
    {
        const string deviceJson =
            """{"device_code":"DC","user_code":"WXYZ","verification_uri":"https://spotify.com/pair","expires_in":600,"interval":5}""";
        var http = new FakeHttpPost((url, _) =>
            url.Contains("device/authorize")
                ? new HttpResult(200, deviceJson)
                : new HttpResult(400, """{"error":"expired_token"}"""));
        var device = new DeviceCodeProvider(http, "c", ["s"], delay: (_, _) => Task.CompletedTask, now: () => DateTimeOffset.UnixEpoch);

        var states = new List<AuthState>();
        var flow = new AuthFlow([new StoredCredentialProvider(() => null), device]);
        using var _ = flow.State.Subscribe(new Sink(states.Add));

        var cred = await flow.AcquireAsync(default);

        Assert.Null(cred);
        Assert.Equal(AuthPhase.ChallengeExpired, flow.Current.Phase);   // a recoverable "regenerate", NOT a generic Failed
        Assert.DoesNotContain(states, s => s.Phase == AuthPhase.Failed);
    }

    sealed class Sink(Action<AuthState> on) : IObserver<AuthState>
    {
        public void OnCompleted() { }
        public void OnError(Exception e) { }
        public void OnNext(AuthState v) => on(v);
    }
}

public class SpotifyAuthSessionTests
{
    static AuthFlow StoredFlow() =>
        new([new StoredCredentialProvider(() => new Credential(CredentialKind.ReusableBlob, "u", "b"))]);

    [Fact]
    public async Task Premium_ConnectsAndAuthenticates()
    {
        var session = new SessionContextHost(new SessionContext("me", "US", "premium", "en", Tier.Premium, false));
        var sess = new SpotifyAuthSession(StoredFlow(), session);

        bool ok = await sess.ConnectAsync();

        Assert.True(ok);
        Assert.Equal(AuthStatus.Authenticated, sess.Status);
        Assert.NotNull(sess.CurrentUser);
        Assert.True(sess.CurrentUser!.IsPremium);
    }

    [Fact]
    public async Task Free_IsRefusedByThePremiumGate()
    {
        var session = new SessionContextHost(new SessionContext("me", "US", "premium", "en", Tier.Free, false));
        var sess = new SpotifyAuthSession(StoredFlow(), session);

        bool ok = await sess.ConnectAsync();

        Assert.False(ok);
        Assert.Equal(AuthStatus.Error, sess.Status);
        Assert.Null(sess.CurrentUser);
    }

    [Fact]
    public async Task ConnectAsync_OnProviderFailure_ResolvesToError_NotStuckOnAuthenticating()
    {
        var session = new SessionContextHost(new SessionContext("me", "US", "premium", "en", Tier.Premium, false));
        var sess = new SpotifyAuthSession(new AuthFlow([new ThrowingProvider()]), session);

        bool ok = await sess.ConnectAsync();

        Assert.False(ok);
        Assert.Equal(AuthStatus.Error, sess.Status);   // not pinned on Authenticating
    }

    sealed class ThrowingProvider : ICredentialProvider
    {
        public string Name => "throws";
        public Task<Credential?> AcquireAsync(IAuthSink sink, CancellationToken ct) => throw new TimeoutException("network");
    }
}
