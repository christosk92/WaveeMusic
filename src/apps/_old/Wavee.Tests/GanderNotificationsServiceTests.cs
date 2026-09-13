using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;

// SpotifyNotificationsService (gander): field parsing (NAVIGATE vs WEBVIEW, multi-user names, missing optionals), the
// 500 → Error and empty → Empty states, and the 5-minute staleness gate. HTTP + clock faked; the parse runs for real
// against the sanitized fixture.
public class GanderNotificationsServiceTests
{
    const string BaseUrl = "https://spclient.test";

    static string Fixture()
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gander-notifications.json"));

    [Fact]
    public void Seed_ParsesFields_PreservesOrder()
    {
        var http = new ScriptExchange((_, _) => Task.FromResult(Ok(Fixture())));
        using var svc = new SpotifyNotificationsService(http, () => BaseUrl);
        WaitUntil(() => svc.State == NotificationFeedState.Populated);

        Assert.Equal(3, svc.Snapshot.Count);

        var a = svc.Snapshot[0];
        Assert.Equal("ntf-1001", a.Id);
        Assert.Equal("fakeuser42 started following you", a.Title);
        // ISO-8601 wire timestamp ("2026-07-09T16:02:24.011Z") parsed to epoch ms.
        Assert.Equal(DateTimeOffset.Parse("2026-07-09T16:02:24.011Z").ToUnixTimeMilliseconds(), a.Timestamp);
        Assert.Equal(SocialActionType.Navigate, a.ActionType);
        Assert.Equal("spotify:user:fakeuser42", a.ActionUri);
        Assert.Equal("https://i.example/img/fakeuser42.jpg", a.ImageUrl);
        Assert.Equal(new[] { "fakeuser42" }, a.UserNames);
        Assert.True(a.IsUnread);
        Assert.Equal("stor-aaa", a.StorageId);

        var b = svc.Snapshot[1];
        Assert.Equal(SocialActionType.NavigateWebview, b.ActionType);
        Assert.Null(b.ImageUrl);
        Assert.Equal(new[] { "Ada", "Grace" }, b.UserNames);
        Assert.False(b.IsUnread);

        var c = svc.Snapshot[2];
        Assert.Null(c.ActionUri);
        Assert.Equal(SocialActionType.NavigateWebview, c.ActionType);   // missing action → web fallback
        Assert.Empty(c.UserNames);
    }

    [Fact]
    public void Seed_Empty_SetsEmpty()
    {
        var http = new ScriptExchange((_, _) => Task.FromResult(Ok("{\"notifications\":[]}")));
        using var svc = new SpotifyNotificationsService(http, () => BaseUrl);
        WaitUntil(() => svc.State == NotificationFeedState.Empty);
        Assert.Empty(svc.Snapshot);
    }

    [Fact]
    public void Seed_500_SetsError()
    {
        var http = new ScriptExchange((_, _) => Task.FromResult(Code(500)));
        using var svc = new SpotifyNotificationsService(http, () => BaseUrl);
        WaitUntil(() => svc.State == NotificationFeedState.Error);
        Assert.Empty(svc.Snapshot);
    }

    [Fact]
    public void EnsureFresh_StalenessGate()
    {
        long now = 1_000_000;
        var http = new ScriptExchange((_, _) => Task.FromResult(Ok(Fixture())));
        using var svc = new SpotifyNotificationsService(http, () => BaseUrl, log: default, clock: () => Volatile.Read(ref now));
        WaitUntil(() => svc.State == NotificationFeedState.Populated);
        Assert.Equal(1, http.Calls);

        Volatile.Write(ref now, 1_000_000 + 60_000);   // +1 min → still fresh
        svc.EnsureFresh();
        Thread.Sleep(50);
        Assert.Equal(1, http.Calls);

        Volatile.Write(ref now, 1_000_000 + 6 * 60_000);   // +6 min → stale → refetch
        svc.EnsureFresh();
        WaitUntil(() => http.Calls >= 2);
        Assert.True(http.Calls >= 2);
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────────────────────────────
    static HttpResp Ok(string json) => new(200, Headers(), Encoding.UTF8.GetBytes(json));
    static HttpResp Code(int status) => new(status, Headers(), Array.Empty<byte>());
    static Dictionary<string, string> Headers() => new(StringComparer.OrdinalIgnoreCase);

    static void WaitUntil(Func<bool> cond, int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < timeoutMs) Thread.Sleep(5);
        Assert.True(cond(), "condition not met within " + timeoutMs + "ms");
    }

    sealed class ScriptExchange : IHttpExchange
    {
        readonly Func<HttpReq, CancellationToken, Task<HttpResp>> _fn;
        int _calls;
        public ScriptExchange(Func<HttpReq, CancellationToken, Task<HttpResp>> fn) => _fn = fn;
        public int Calls => Volatile.Read(ref _calls);
        public Task<HttpResp> SendAsync(HttpReq req, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return _fn(req, ct);
        }
    }
}
