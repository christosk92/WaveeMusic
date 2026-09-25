// ── Wavee.Tests/CaptureRulesTests.cs — `CaptureRules.PriorityOf` (§3.5, §6.3) ───────────────────────────────────────

using Xunit;

namespace Wavee.Tests;

public class CaptureRulesTests
{
    [Fact]
    public void Dealer_ping_pong_with_no_uri_is_low_priority()
    {
        Assert.Equal(CapturePriority.Low, CaptureRules.PriorityOf(CaptureKind.DealerFrameIn, uri: null));
        Assert.Equal(CapturePriority.Low, CaptureRules.PriorityOf(CaptureKind.DealerFrameOut, uri: null));
    }

    [Fact]
    public void A_named_dealer_frame_is_normal_priority()
    {
        Assert.Equal(CapturePriority.Normal, CaptureRules.PriorityOf(CaptureKind.DealerFrameIn, uri: "hm://connect-state/v1/cluster"));
        Assert.Equal(CapturePriority.Normal, CaptureRules.PriorityOf(CaptureKind.DealerFrameOut, uri: "hm://pusher/v1/connections"));
    }

    [Fact]
    public void Collection_diff_http_calls_are_low_priority()
    {
        Assert.Equal(CapturePriority.Low, CaptureRules.PriorityOf(CaptureKind.HttpCall, uri: "/collection/v2/tracks"));
    }

    [Fact]
    public void Every_other_http_call_is_normal_priority()
    {
        Assert.Equal(CapturePriority.Normal, CaptureRules.PriorityOf(CaptureKind.HttpCall, uri: "/connect-state/v1/devices/abc"));
        Assert.Equal(CapturePriority.Normal, CaptureRules.PriorityOf(CaptureKind.HttpCall, uri: null));
    }

    [Fact]
    public void Action_and_connect_state_put_records_are_always_normal_priority()
    {
        Assert.Equal(CapturePriority.Normal, CaptureRules.PriorityOf(CaptureKind.ActionInvoke, uri: null));
        Assert.Equal(CapturePriority.Normal, CaptureRules.PriorityOf(CaptureKind.ConnectStatePut, uri: null));
        Assert.Equal(CapturePriority.Normal, CaptureRules.PriorityOf(CaptureKind.ConnectStatePutResponse, uri: null));
    }

    // ── IsAuthEndpoint — security requirement, 2026-09-23: never capture a credential/token endpoint's body ────────

    [Theory]
    [InlineData("login5.spotify.com", "/v3/login")]
    [InlineData("clienttoken.spotify.com", "/v1/clienttoken")]
    [InlineData("accounts.spotify.com", "/api/token")]
    [InlineData("accounts.spotify.com", "/authorize")]
    [InlineData("accounts.spotify.com", "/oauth2/device/authorize")]
    [InlineData("LOGIN5.SPOTIFY.COM", "/v3/login")] // host match is case-insensitive
    public void Named_credential_hosts_are_auth_endpoints(string host, string path)
        => Assert.True(CaptureRules.IsAuthEndpoint(host, path));

    [Theory]
    [InlineData("api.spotify.com", "/v1/me/login5")]           // path fallback: "login5" anywhere in the path
    [InlineData("api.spotify.com", "/internal/clienttoken/x")]
    [InlineData("api.spotify.com", "/oauth/token")]
    [InlineData("api.spotify.com", "/api/token")]
    [InlineData("api.spotify.com", "/v1/stored-credential")]
    public void A_path_shaped_like_a_credential_exchange_is_an_auth_endpoint_under_any_host(string host, string path)
        => Assert.True(CaptureRules.IsAuthEndpoint(host, path));

    [Theory]
    [InlineData("api.spotify.com", "/v1/me/player")]
    [InlineData("spclient.spotify.com", "/connect-state/v1/devices/abc")]
    [InlineData(null, null)]
    [InlineData("gew1-spclient.spotify.com", "/connect-state/v1/player/command")]
    public void An_ordinary_endpoint_is_not_an_auth_endpoint(string? host, string? path)
        => Assert.False(CaptureRules.IsAuthEndpoint(host, path));
}
