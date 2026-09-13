using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Wavee.Module.YouTube;
using Wavee.Sdk;
using Wavee.Tests.Modules.Fixtures;
using Xunit;

namespace Wavee.Tests.Modules;

/// <summary>
/// The YouTube module, driven through <see cref="ModuleTestHost"/> over a scripted transport. Nothing here touches
/// the network: every InnerTube response, channel page and HLS master is a fixture.
/// </summary>
public class YouTubeModuleTests : IDisposable
{
    private const string Id = YouTubeFixtures.VideoId;

    private readonly List<string> _dataDirs = [];

    private (ModuleTestHost Host, ScriptedHttpHandler Http) Make(ScriptedHttpHandler http)
        => (new ModuleTestHost(new YouTubeModule(http), TestDataDir()), http);

    /// <summary>
    /// A data dir that holds no clients.json (so the built-in table runs) and, crucially, no <c>session.json</c> from
    /// any other test.
    /// <para>
    /// ISOLATION: the module now PERSISTS a preferred client, a visitor id and a wall cooldown per data dir. A shared
    /// dir would make this suite order-dependent in the worst way — one test's cooldown would make the next test's
    /// resolve fail before it sent anything, and one test's preferred client would silently reorder the next test's
    /// client walk. So each host gets its own fresh directory (xUnit builds one instance of this class per test, so
    /// the list below is per-test) and the class deletes them on the way out. The alternative — injecting a store and
    /// a clock — was rejected because it would test a seam instead of the file the module actually writes.
    /// </para>
    /// </summary>
    private string TestDataDir()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wavee-yt-tests",
            Guid.NewGuid().ToString("n"));
        System.IO.Directory.CreateDirectory(dir);
        _dataDirs.Add(dir);
        return dir;
    }

    /// <summary>Removes the per-test data dirs. Best effort: a leaked temp dir is not worth failing a test over.</summary>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
        foreach (string dir in _dataDirs)
        {
            try
            {
                System.IO.Directory.Delete(dir, recursive: true);
            }
            catch (System.IO.IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The session file the module writes into a data dir.</summary>
    private static string SessionFile(string dataDir)
        => System.IO.Path.Combine(dataDir, YouTubeSessionStore.FileName);

    // ---- match -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=tRsQsTMvPNg")]
    [InlineData("https://www.youtube.com/watch?v=tRsQsTMvPNg&t=42s")]
    [InlineData("https://m.youtube.com/watch?v=tRsQsTMvPNg")]
    [InlineData("https://music.youtube.com/watch?v=tRsQsTMvPNg&list=RDAMVM")]
    [InlineData("https://youtu.be/tRsQsTMvPNg")]
    [InlineData("https://youtu.be/tRsQsTMvPNg?t=10")]
    [InlineData("https://www.youtube.com/live/tRsQsTMvPNg")]
    [InlineData("https://www.youtube.com/shorts/tRsQsTMvPNg")]
    [InlineData("https://www.youtube.com/embed/tRsQsTMvPNg")]
    [InlineData("https://www.youtube.com/v/tRsQsTMvPNg")]
    [InlineData("https://www.youtube-nocookie.com/embed/tRsQsTMvPNg")]
    [InlineData("youtube.com/watch?v=tRsQsTMvPNg")]
    [InlineData("tRsQsTMvPNg")]
    public async Task Match_AcceptsEveryVideoUrlForm(string input)
    {
        (ModuleTestHost host, _) = Make(new ScriptedHttpHandler());

        MatchResult? match = await host.MatchAsync(input, TestContext.Current.CancellationToken);

        Assert.NotNull(match);
        Assert.Equal(Id, match.PlayableId);
        Assert.Equal(MediaForm.Video, match.Form);
    }

    [Theory]
    [InlineData("https://www.twitch.tv/somebody")]
    [InlineData("https://example.org/stream.mp3")]
    [InlineData("https://www.youtube.com/playlist?list=PL1234")]
    [InlineData("https://www.youtube.com/embed/videoseries?list=PL1234")]
    [InlineData("")]
    [InlineData("not a link")]
    public async Task Match_DeclinesEverythingElse(string input)
    {
        (ModuleTestHost host, _) = Make(new ScriptedHttpHandler());

        Assert.Null(await host.MatchAsync(input, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Match_ChannelLivePage_ReadsCurrentVideoEndpointFirst()
    {
        var http = new ScriptedHttpHandler()
            .OnUrl("/live", HttpStatusCode.OK, YouTubeFixtures.ChannelLiveHtmlWithEndpoint, "text/html");
        (ModuleTestHost host, _) = Make(http);

        MatchResult? match = await host.MatchAsync("https://www.youtube.com/@anthropic/live",
            TestContext.Current.CancellationToken);

        Assert.NotNull(match);
        Assert.Equal(Id, match.PlayableId);
        Assert.True(match.IsLive);
        Assert.Equal(YouTubeModule.DesktopUserAgent, http.Requests[0].Header("User-Agent"));
    }

    [Fact]
    public async Task Match_ChannelLivePage_FallsBackToCanonical()
    {
        var http = new ScriptedHttpHandler()
            .OnUrl("/live", HttpStatusCode.OK, YouTubeFixtures.ChannelLiveHtmlCanonicalOnly, "text/html");
        (ModuleTestHost host, _) = Make(http);

        MatchResult? match = await host.MatchAsync("https://www.youtube.com/channel/UCAAAAAAAAAAAAAAAAAAAAA/live",
            TestContext.Current.CancellationToken);

        Assert.Equal(Id, match!.PlayableId);
    }

    [Fact]
    public async Task Match_ChannelLivePage_OfflineIsOffline()
    {
        var http = new ScriptedHttpHandler()
            .OnUrl("/live", HttpStatusCode.OK, YouTubeFixtures.ChannelLiveHtmlOffline, "text/html");
        (ModuleTestHost host, _) = Make(http);

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.MatchAsync("https://www.youtube.com/@anthropic/live", TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.Offline, ex.Code);
    }

    // ---- resolve: the happy path and the request shape ----------------------------------------------------------

    [Fact]
    public async Task Resolve_UsesVisionOsFirstAndStops()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk).WithManifest();
        (ModuleTestHost host, _) = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync(Id, TestContext.Current.CancellationToken);

        RecordedRequest[] players = PlayerCalls(http);
        Assert.Single(players);
        Assert.Contains("\"clientName\":\"VISIONOS\"", players[0].Body, StringComparison.Ordinal);
        Assert.Equal("101", players[0].Header("X-YouTube-Client-Name"));
        Assert.Equal("1.02", players[0].Header("X-YouTube-Client-Version"));
        Assert.Equal("https://www.youtube.com", players[0].Header("Origin"));

        Assert.Equal(YouTubeFixtures.HlsManifestUrl, resolved.Media.Url);
        Assert.Equal(MediaLocator.ContainerHls, resolved.Media.Container);
        Assert.Equal(MediaForm.Video, resolved.Form);
        Assert.True(resolved.IsLive);
        Assert.Equal(0, resolved.DurationMs);
        Assert.Equal("Claude FM", resolved.Title);
        Assert.Equal(new[] { "Anthropic" }, resolved.Artists);
        Assert.Equal("https://i.ytimg.com/vi/tRsQsTMvPNg/maxresdefault.jpg", resolved.ArtworkUrl);
    }

    [Fact]
    public async Task Resolve_RequestBodyOmitsSignatureTimestampAndParams()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk).WithManifest();
        (ModuleTestHost host, _) = Make(http);

        await host.ResolveAsync(Id, TestContext.Current.CancellationToken);

        string body = PlayerCalls(http)[0].Body;
        Assert.Contains("\"contentCheckOk\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"racyCheckOk\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"html5Preference\":\"HTML5_PREF_WANTS\"", body, StringComparison.Ordinal);
        Assert.Contains("\"timeZone\":\"UTC\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("signatureTimestamp", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"params\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("serviceIntegrityDimensions", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_AndroidBlockCarriesTheSdkVersion()
    {
        var http = new ScriptedHttpHandler()
            .OnBody("\"clientName\":\"VISIONOS\"", HttpStatusCode.OK, YouTubeFixtures.PlayerUnplayable)
            .OnBody("\"clientName\":\"ANDROID\"", HttpStatusCode.OK, YouTubeFixtures.PlayerLiveOk)
            .WithManifest();
        (ModuleTestHost host, _) = Make(http);

        await host.ResolveAsync(Id, TestContext.Current.CancellationToken);

        RecordedRequest android = PlayerCalls(http)[1];
        Assert.Contains("\"androidSdkVersion\":30", android.Body, StringComparison.Ordinal);
        Assert.Equal("3", android.Header("X-YouTube-Client-Name"));
        Assert.Equal("com.google.android.youtube/21.26.364 (Linux; U; Android 11) gzip",
            android.Header("User-Agent"));
    }

    [Fact]
    public async Task Resolve_VodKeepsItsDuration()
    {
        var http = Player(YouTubeFixtures.PlayerVodOk).WithManifest();
        (ModuleTestHost host, _) = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync(Id, TestContext.Current.CancellationToken);

        Assert.False(resolved.IsLive);
        Assert.Equal(3_672_000, resolved.DurationMs);
    }

    // ---- resolve: the fallback table ----------------------------------------------------------------------------

    [Fact]
    public async Task Resolve_VideoIdMismatchAdvancesToTheNextClient()
    {
        var http = new ScriptedHttpHandler()
            .OnBody("\"clientName\":\"VISIONOS\"", HttpStatusCode.OK, YouTubeFixtures.PlayerVideoIdMismatch)
            .OnBody("\"clientName\":\"ANDROID\"", HttpStatusCode.OK, YouTubeFixtures.PlayerLiveOk)
            .WithManifest();
        (ModuleTestHost host, _) = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync(Id, TestContext.Current.CancellationToken);

        Assert.Equal(2, PlayerCalls(http).Length);
        Assert.Equal(Id, resolved.PlayableId);
    }

    [Fact]
    public async Task Resolve_SabrOnlyOnEveryClientIsUnavailable()
    {
        var http = Player(YouTubeFixtures.PlayerSabrOnly);
        (ModuleTestHost host, _) = Make(http);

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync(Id, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.Unavailable, ex.Code);
        Assert.Contains("SABR", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, PlayerCalls(http).Length);
    }

    /// <summary>
    /// The wall costs AT MOST TWO <c>/player</c> calls, not three. It used to <c>continue</c> through the whole
    /// table, so one press of Play spent three flagged requests — which is how the 2026-08-23 session made its own
    /// address hot. The wall is still worth one alternate client (it was per-CLIENT on 2026-08-22), and no more.
    /// </summary>
    [Fact]
    public async Task Resolve_BotWallCostsAtMostTwoPlayerCalls()
    {
        var http = Player(YouTubeFixtures.PlayerBotWall);
        (ModuleTestHost host, _) = Make(http);

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync(Id, TestContext.Current.CancellationToken));

        Assert.Equal(2, PlayerCalls(http).Length);
        Assert.Equal("101", PlayerCalls(http)[0].Header("X-YouTube-Client-Name"));
        Assert.Equal("3", PlayerCalls(http)[1].Header("X-YouTube-Client-Name"));

        // Every client asked was walled, so this is the blocked verdict — and the message asserts nothing about the
        // user's network and promises nothing about signing in.
        Assert.Equal(ModuleErrorCode.Unavailable, ex.Code);
        Assert.Equal(
            "YouTube is challenging this device as a bot. This usually clears on its own; a VPN or shared " +
            "connection makes it more likely.",
            ex.Message);
        Assert.DoesNotContain("datacenter", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signed-in", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Once walled, a user mashing Play must cost YouTube NOTHING: the cooldown is checked before any
    /// request is built, and the second resolve adds no <c>/player</c> call at all.</summary>
    [Fact]
    public async Task Resolve_InsideTheCooldownIssuesNoRequestAtAll()
    {
        var http = Player(YouTubeFixtures.PlayerBotWall);
        (ModuleTestHost host, _) = Make(http);

        await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync(Id, TestContext.Current.CancellationToken));
        int afterTheWall = http.Requests.Count;

        ModuleException again = await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync(Id, TestContext.Current.CancellationToken));

        Assert.Equal(afterTheWall, http.Requests.Count);
        Assert.Equal(ModuleErrorCode.Transient, again.Code);
        Assert.Equal("YouTube is rate-limiting this device. Try again in a minute.", again.Message);
    }

    /// <summary>The cooldown is persisted, not just held in memory: restarting the module (a new instance over the
    /// same data dir) must not be a way to keep hammering.</summary>
    [Fact]
    public async Task Resolve_TheCooldownSurvivesAModuleRestart()
    {
        string dataDir = TestDataDir();
        var first = Player(YouTubeFixtures.PlayerBotWall);
        var firstHost = new ModuleTestHost(new YouTubeModule(first), dataDir);

        await Assert.ThrowsAsync<ModuleException>(() =>
            firstHost.ResolveAsync(Id, TestContext.Current.CancellationToken));
        Assert.True(System.IO.File.Exists(SessionFile(dataDir)));

        var restarted = Player(YouTubeFixtures.PlayerBotWall);
        var restartedHost = new ModuleTestHost(new YouTubeModule(restarted), dataDir);

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            restartedHost.ResolveAsync(Id, TestContext.Current.CancellationToken));

        Assert.Empty(restarted.Requests);
        Assert.Equal(ModuleErrorCode.Transient, ex.Code);
    }

    /// <summary>
    /// A bare <c>LOGIN_REQUIRED</c> — no age marker, no age wording, no "bot" — used to be silently terminal: the age
    /// predicate ended in an unguarded <c>status == LOGIN_REQUIRED</c> and only the accident of being tested second
    /// kept it off the real bot wall. It is a wall, so it costs one alternate client and a retryable/blocked verdict,
    /// never <c>NeedsAuth</c>.
    /// </summary>
    [Fact]
    public async Task Resolve_BareLoginRequiredIsAWallAndNotAnAgeGate()
    {
        var http = Player(YouTubeFixtures.PlayerLoginRequiredBare);
        (ModuleTestHost host, _) = Make(http);

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync(Id, TestContext.Current.CancellationToken));

        Assert.NotEqual(ModuleErrorCode.NeedsAuth, ex.Code);
        Assert.Equal(ModuleErrorCode.Unavailable, ex.Code);
        Assert.Equal(2, PlayerCalls(http).Length);
    }

    [Fact]
    public async Task Resolve_BotWallOnTheFirstClientFallsThroughToTheNext()
    {
        var http = new ScriptedHttpHandler()
            .On(r => r.Url.Contains("youtubei/v1/player", StringComparison.Ordinal)
                     && r.Header("X-YouTube-Client-Name") == "101",
                _ => new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(YouTubeFixtures.PlayerBotWall,
                        System.Text.Encoding.UTF8, "application/json"),
                })
            .OnUrl("youtubei/v1/player", HttpStatusCode.OK, YouTubeFixtures.PlayerLiveOk, "application/json")
            .WithManifest();
        (ModuleTestHost host, _) = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync(Id, TestContext.Current.CancellationToken);

        Assert.True(resolved.IsLive);
        Assert.Equal(2, PlayerCalls(http).Length);
        Assert.Equal("101", PlayerCalls(http)[0].Header("X-YouTube-Client-Name"));
        Assert.Equal("3", PlayerCalls(http)[1].Header("X-YouTube-Client-Name"));
    }

    // ---- resolve: the session identity ---------------------------------------------------------------------------

    /// <summary>
    /// A2, the highest-value change in this workstream: the client that last produced a playable manifest is asked
    /// FIRST next time. The table order put VISIONOS first, and on 2026-08-23 VISIONOS was walled on 9 of 9 attempts
    /// before ANDROID served — so every play burned one flagged request before it started. It no longer does.
    /// </summary>
    [Fact]
    public async Task Resolve_AsksTheClientThatLastWorkedFirst()
    {
        var http = new ScriptedHttpHandler()
            .OnBody("\"clientName\":\"VISIONOS\"", HttpStatusCode.OK, YouTubeFixtures.PlayerBotWall)
            .OnBody("\"clientName\":\"ANDROID\"", HttpStatusCode.OK, YouTubeFixtures.PlayerLiveOk)
            .WithManifest();
        (ModuleTestHost host, _) = Make(http);

        await host.ResolveAsync(Id, TestContext.Current.CancellationToken);
        Assert.Equal(2, PlayerCalls(http).Length);           // walled on visionos, served by android

        await host.ResolveAsync(Id, TestContext.Current.CancellationToken);

        // The second play costs ONE request, and it is the client that is known to work.
        Assert.Equal(3, PlayerCalls(http).Length);
        Assert.Equal("3", PlayerCalls(http)[2].Header("X-YouTube-Client-Name"));
        Assert.Contains("\"clientName\":\"ANDROID\"", PlayerCalls(http)[2].Body, StringComparison.Ordinal);
    }

    /// <summary>A1: InnerTube hands back a visitor id on every response and expects it echoed on the next request, as
    /// BOTH the header and the client block. Until it was read, every single call presented as a brand-new anonymous
    /// client — which is exactly the shape an anti-bot system is looking for.</summary>
    [Fact]
    public async Task Resolve_EchoesTheVisitorIdItWasGivenOnTheNextRequest()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk).WithManifest();
        (ModuleTestHost host, _) = Make(http);

        await host.ResolveAsync(Id, TestContext.Current.CancellationToken);
        await host.ResolveAsync(Id, TestContext.Current.CancellationToken);

        // Nothing to present as on the very first call of a session.
        Assert.Null(PlayerCalls(http)[0].Header("X-Goog-Visitor-Id"));
        Assert.DoesNotContain("visitorData", PlayerCalls(http)[0].Body, StringComparison.Ordinal);

        Assert.Equal(YouTubeFixtures.VisitorData, PlayerCalls(http)[1].Header("X-Goog-Visitor-Id"));
        Assert.Contains("\"visitorData\":\"" + YouTubeFixtures.VisitorData + "\"", PlayerCalls(http)[1].Body,
            StringComparison.Ordinal);
    }

    /// <summary>A burned visitor id is worse than none: re-presenting the identity YouTube just refused is the one
    /// thing guaranteed to be refused again.</summary>
    [Fact]
    public async Task Resolve_DropsTheVisitorIdThatGotWalled()
    {
        var http = new ScriptedHttpHandler()
            .OnBody("\"clientName\":\"VISIONOS\"", HttpStatusCode.OK, YouTubeFixtures.PlayerBotWall)
            .OnBody("\"clientName\":\"ANDROID\"", HttpStatusCode.OK, YouTubeFixtures.PlayerLiveOk)
            .WithManifest();
        (ModuleTestHost host, _) = Make(http);

        await host.ResolveAsync(Id, TestContext.Current.CancellationToken);

        // The walled response carried a visitorData; the alternate client must NOT wear it.
        Assert.Null(PlayerCalls(http)[1].Header("X-Goog-Visitor-Id"));
        Assert.DoesNotContain("visitorData", PlayerCalls(http)[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_AgeGateIsNeedsAuth()
    {
        var http = Player(YouTubeFixtures.PlayerAgeGate);
        (ModuleTestHost host, _) = Make(http);

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync(Id, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.NeedsAuth, ex.Code);
        Assert.Single(PlayerCalls(http));
    }

    [Fact]
    public async Task Resolve_LiveStreamOfflineIsOfflineWithTheStartTime()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOffline);
        (ModuleTestHost host, _) = Make(http);

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync(Id, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.Offline, ex.Code);
        Assert.Contains("2026-09-01", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_UnplayableWalksTheTableThenReportsTheReasonVerbatim()
    {
        var http = Player(YouTubeFixtures.PlayerUnplayable);
        (ModuleTestHost host, _) = Make(http);

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync(Id, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.Unavailable, ex.Code);
        Assert.Equal("This video is not available on this app.", ex.Message);
        Assert.Equal(3, PlayerCalls(http).Length);
    }

    [Fact]
    public async Task Resolve_ManifestPreflight403AdvancesToTheNextClient()
    {
        int manifestCalls = 0;
        var http = new ScriptedHttpHandler()
            .OnBody("\"clientName\":\"VISIONOS\"", HttpStatusCode.OK, YouTubeFixtures.PlayerLiveOk)
            .OnBody("\"clientName\":\"ANDROID\"", HttpStatusCode.OK, YouTubeFixtures.PlayerLiveOk);
        http.On(r => r.Url.Contains("manifest.googlevideo.com", StringComparison.Ordinal), _ =>
        {
            manifestCalls++;
            return manifestCalls == 1
                ? ScriptedHttpHandler.Respond(HttpStatusCode.Forbidden, "")
                : ScriptedHttpHandler.Respond(HttpStatusCode.OK, YouTubeFixtures.HlsMaster,
                    "application/vnd.apple.mpegurl");
        });
        (ModuleTestHost host, _) = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync(Id, TestContext.Current.CancellationToken);

        Assert.Equal(2, PlayerCalls(http).Length);
        Assert.Equal(2, manifestCalls);
        Assert.Equal(YouTubeFixtures.HlsManifestUrl, resolved.Media.Url);
    }

    [Fact]
    public async Task Resolve_NonPlaylistManifestBodyIsUnavailable()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk)
            .OnUrl("manifest.googlevideo.com", HttpStatusCode.OK, "<html>nope</html>", "text/html");
        (ModuleTestHost host, _) = Make(http);

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync(Id, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.Unavailable, ex.Code);
        Assert.Single(PlayerCalls(http));
    }

    [Fact]
    public async Task Resolve_RejectsAnIdThatIsNotAVideoId()
    {
        (ModuleTestHost host, _) = Make(new ScriptedHttpHandler());

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync("not-an-id", TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.NotOwned, ex.Code);
    }

    // ---- expiry ------------------------------------------------------------------------------------------------

    [Fact]
    public void ExpiresAt_TakesTheEarlierOfTheSignedExpiryAndTheSessionLifetime()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_767_000_000);

        // The signed /expire/ is earlier than now + 21540.
        long? signedWins = YouTubeModule.ExpiresAt("https://x/expire/1767010000/playlist/index.m3u8", "21540", now);
        Assert.Equal((1_767_010_000L - 600) * 1000L, signedWins!.Value);

        // The session lifetime is earlier than the signed expiry.
        long? lifetimeWins = YouTubeModule.ExpiresAt("https://x/expire/1799999999/playlist/index.m3u8", "600", now);
        Assert.Equal((1_767_000_600L - 600) * 1000L, lifetimeWins!.Value);
    }

    [Fact]
    public void ExpiresAt_IsNullWhenYouTubeSignedNeither()
        => Assert.Null(YouTubeModule.ExpiresAt("https://x/playlist/index.m3u8", null,
            DateTimeOffset.FromUnixTimeSeconds(1_767_000_000)));

    [Fact]
    public async Task Resolve_PublishesTheExpiryTheHostReResolvesOn()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk).WithManifest();
        (ModuleTestHost host, _) = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync(Id, TestContext.Current.CancellationToken);

        // The fixture's manifest signs /expire/1767225600/, which is inside the 21540 s session window here.
        Assert.NotNull(resolved.ExpiresAtUnixMs);
        Assert.True(resolved.ExpiresAtUnixMs!.Value <= (1_767_225_600L - 600) * 1000L);
    }

    // ---- pages -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Page_Video_IsAWatchDocumentWithTheHeroFactsDescriptionAndActions()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk);
        (ModuleTestHost host, _) = Make(http);

        ModulePageDoc? page = await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(ModulePageDoc.TemplateWatch, page!.Template);
        Assert.Equal("Claude FM", page.Hero!.Title);
        Assert.Equal("Anthropic", page.Hero.Subtitle);
        Assert.True(page.Hero.IsLive);
        Assert.Equal("https://i.ytimg.com/vi/tRsQsTMvPNg/maxresdefault.jpg", page.Hero.ImageUrl);

        PageAction play = page.Actions.Single(a => a.Kind == PageAction.KindPlay);
        Assert.Equal(Id, play.PlayableId);
        Assert.True(play.Primary);

        PageAction open = page.Actions.Single(a => a.Kind == PageAction.KindOpenUrl);
        Assert.Equal("https://www.youtube.com/watch?v=" + Id, open.Url);
        Assert.Equal("Open on YouTube", open.Label);

        // The facts and description sections are untouched by the watch layout: the new template folds them into its
        // description card, and an app that does not know "watch" still renders exactly this document the old way.
        PageSection facts = page.Sections.Single(x => x.Kind == PageSection.KindFacts);
        Assert.Contains(facts.Rows!, r => r[0] == "Views" && r[1] == "1,234");

        PageSection text = page.Sections.Single(x => x.Kind == PageSection.KindText);
        Assert.Contains("continuous broadcast", text.Text!, StringComparison.Ordinal);

        // The one-card channel shelf stays as the fallback for an app that does not read Hero.SubtitleEntityId.
        PageItem channel = page.Sections.Single(x => x.Kind == PageSection.KindCards).Items!.Single();
        Assert.Equal("channel:" + YouTubeFixtures.ChannelId, channel.EntityId);
    }

    [Fact]
    public async Task Page_Video_CarriesTheOwnerAvatarAndTheSubtitleEntityId()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk);
        (ModuleTestHost host, _) = Make(http);

        ModulePageDoc? page = await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);

        // The avatar exists nowhere in the player response — it is the whole reason /next is called.
        Assert.Equal(YouTubeFixtures.ChannelAvatarUrl, page!.Hero!.AvatarUrl);
        Assert.Equal("channel:" + YouTubeFixtures.ChannelId, page.Hero.SubtitleEntityId);
        Assert.Equal("https://i.ytimg.com/vi/tRsQsTMvPNg/maxresdefault.jpg", page.Hero.ImageUrl);
    }

    [Fact]
    public async Task Page_Video_LivePrefersTheWatchingCountOverLifetimeViews()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk);
        (ModuleTestHost host, _) = Make(http);

        ModulePageDoc? page = await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);

        string meta = page!.Hero!.MetaLine!;
        Assert.Contains("Live now", meta, StringComparison.Ordinal);
        Assert.Contains("12,345 watching now", meta, StringComparison.Ordinal);
        Assert.Contains("Started streaming 3 hours ago", meta, StringComparison.Ordinal);
        // videoDetails.viewCount on a broadcast is a lifetime total; printing it beside a LIVE badge would be a lie.
        Assert.DoesNotContain("1,234 views", meta, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rail as YouTube actually answers it today: <c>lockupViewModel</c> entries. A real WEB capture taken
    /// 2026-08-23 held 20 of them and zero <c>compactVideoRenderer</c>, so this is the branch that decides whether a
    /// real page has a shelf at all.
    /// </summary>
    [Fact]
    public async Task Page_Video_BuildsTheUpNextShelfFromALockupRail()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk);
        (ModuleTestHost host, _) = Make(http);

        ModulePageDoc? page = await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);

        PageSection shelf = page!.Sections.Single(x => x.Kind == PageSection.KindPlayables);
        Assert.Equal("Up next", shelf.Title);

        // Four rail entries in, two cards out: the playlist lockup (its contentId is a list id, not a video) and the
        // continuation are both skipped.
        Assert.Equal(2, shelf.Items!.Length);

        PageItem vod = shelf.Items[0];
        Assert.Equal("A recorded talk", vod.Title);
        Assert.Equal("Anthropic", vod.Subtitle);                       // metadata row 0
        Assert.Equal(YouTubeFixtures.RelatedVodId, vod.PlayableId);     // contentId
        Assert.Equal("video:" + YouTubeFixtures.RelatedVodId, vod.EntityId);
        Assert.Equal(MediaForm.Video, vod.Form);
        Assert.False(vod.IsLive);
        Assert.Equal("1:01:12", vod.Meta);                              // the thumbnail overlay badge
        Assert.Equal(YouTubeFixtures.RelatedVodThumbnailUrl, vod.ImageUrl);

        PageItem live = shelf.Items[1];
        Assert.Equal("Another broadcast", live.Title);
        Assert.Equal("Someone Else", live.Subtitle);
        Assert.Equal(YouTubeFixtures.RelatedLiveId, live.PlayableId);
        Assert.True(live.IsLive);
        Assert.Equal("4,200 watching", live.Meta);
        Assert.Equal(YouTubeFixtures.RelatedLiveThumbnailUrl, live.ImageUrl);
    }

    /// <summary>The same rail in the older <c>compactVideoRenderer</c> spelling. Both are in flight upstream, so the
    /// module reads whichever arrived and the two produce the same shelf.</summary>
    [Fact]
    public async Task Page_Video_FallsBackToACompactVideoRendererRail()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk, YouTubeFixtures.NextWatchCompactRail);
        (ModuleTestHost host, _) = Make(http);

        ModulePageDoc? page = await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);

        PageSection shelf = page!.Sections.Single(x => x.Kind == PageSection.KindPlayables);
        Assert.Equal("Up next", shelf.Title);
        Assert.Equal(2, shelf.Items!.Length);

        PageItem vod = shelf.Items[0];
        Assert.Equal("A recorded talk", vod.Title);
        Assert.Equal("Anthropic", vod.Subtitle);
        Assert.Equal(YouTubeFixtures.RelatedVodId, vod.PlayableId);
        Assert.Equal("video:" + YouTubeFixtures.RelatedVodId, vod.EntityId);
        Assert.Equal(MediaForm.Video, vod.Form);
        Assert.False(vod.IsLive);
        Assert.Equal("1:01:12", vod.Meta);
        Assert.Equal(YouTubeFixtures.RelatedVodThumbnailUrl, vod.ImageUrl);

        PageItem live = shelf.Items[1];
        Assert.Equal(YouTubeFixtures.RelatedLiveId, live.PlayableId);
        Assert.True(live.IsLive);
        Assert.Equal("4,200 watching", live.Meta);
        Assert.Equal(YouTubeFixtures.RelatedLiveThumbnailUrl, live.ImageUrl);
    }

    [Fact]
    public async Task Page_Video_LivePageExpiresWithinTheMinute()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk);
        (ModuleTestHost host, _) = Make(http);
        long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        ModulePageDoc? page = await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);

        Assert.NotNull(page!.ExpiresAtUnixMs);
        Assert.InRange(page.ExpiresAtUnixMs!.Value, before, before + YouTubeModule.LivePageTtlMs + 10_000);
    }

    [Fact]
    public async Task Page_Video_VodKeepsTheDefaultCacheWindow()
    {
        var http = Player(YouTubeFixtures.PlayerVodOk, YouTubeFixtures.NextWatchVod);
        (ModuleTestHost host, _) = Make(http);

        ModulePageDoc? page = await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);

        Assert.Null(page!.ExpiresAtUnixMs);
        Assert.Equal("Video", page.Hero!.Eyebrow);
        // The count is not live here, so the lifetime total is the honest one to print.
        Assert.Contains("987,654 views", page.Hero.MetaLine!, StringComparison.Ordinal);
        Assert.Contains("Aug 20, 2026", page.Hero.MetaLine!, StringComparison.Ordinal);
        Assert.DoesNotContain(page.Sections, x => x.Kind == PageSection.KindPlayables);
    }

    /// <summary>
    /// The honesty guarantee: /next is enrichment, never a dependency. Whatever it does — refuse, answer garbage,
    /// answer a shape we no longer recognise, or never answer at all — the page /player served is unchanged.
    /// </summary>
    [Theory]
    [InlineData("status500")]
    [InlineData("garbage")]
    [InlineData("unknownShape")]
    [InlineData("timeout")]
    public async Task Page_Video_NextFailureCostsOnlyTheEnrichment(string failure)
    {
        var http = new ScriptedHttpHandler();
        switch (failure)
        {
            case "status500":
                http.OnUrl("youtubei/v1/next", HttpStatusCode.InternalServerError, "", "text/plain");
                break;
            case "garbage":
                http.OnUrl("youtubei/v1/next", HttpStatusCode.OK, YouTubeFixtures.NextGarbage, "application/json");
                break;
            case "unknownShape":
                http.OnUrl("youtubei/v1/next", HttpStatusCode.OK, YouTubeFixtures.NextWatchUnknownShape,
                    "application/json");
                break;
            default:
                http.On(r => r.Url.Contains("youtubei/v1/next", StringComparison.Ordinal),
                    _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout",
                        new TimeoutException()));
                break;
        }

        http.OnUrl("youtubei/v1/player", HttpStatusCode.OK, YouTubeFixtures.PlayerLiveOk, "application/json");
        (ModuleTestHost host, _) = Make(http);

        ModulePageDoc? page = await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Single(NextCalls(http));
        Assert.Equal(ModulePageDoc.TemplateWatch, page!.Template);
        Assert.Equal("Claude FM", page.Hero!.Title);
        Assert.Equal("Anthropic", page.Hero.Subtitle);
        Assert.Equal("channel:" + YouTubeFixtures.ChannelId, page.Hero.SubtitleEntityId);
        Assert.True(page.Hero.IsLive);
        Assert.Equal("https://i.ytimg.com/vi/tRsQsTMvPNg/maxresdefault.jpg", page.Hero.ImageUrl);

        // Everything /next would have added, and only that, is gone.
        Assert.Null(page.Hero.AvatarUrl);
        Assert.DoesNotContain("watching", page.Hero.MetaLine!, StringComparison.Ordinal);
        Assert.DoesNotContain(page.Sections, x => x.Kind == PageSection.KindPlayables);

        // The sections /player paid for are untouched.
        PageSection facts = page.Sections.Single(x => x.Kind == PageSection.KindFacts);
        Assert.Contains(facts.Rows!, r => r[0] == "Views" && r[1] == "1,234");
        Assert.Contains("continuous broadcast",
            page.Sections.Single(x => x.Kind == PageSection.KindText).Text!, StringComparison.Ordinal);
        Assert.Equal("channel:" + YouTubeFixtures.ChannelId,
            page.Sections.Single(x => x.Kind == PageSection.KindCards).Items!.Single().EntityId);
    }

    /// <summary>Without /next, the date and the channel id fall back to the microformat the player response already
    /// carried — no extra request, which is why those members are modelled at all.</summary>
    [Fact]
    public async Task Page_Video_VodFallsBackToTheMicroformatDateAndChannelId()
    {
        var http = new ScriptedHttpHandler()
            .OnUrl("youtubei/v1/next", HttpStatusCode.InternalServerError, "", "text/plain")
            .OnUrl("youtubei/v1/player", HttpStatusCode.OK, YouTubeFixtures.PlayerVodOk, "application/json");
        (ModuleTestHost host, _) = Make(http);

        ModulePageDoc? page = await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);

        // PlayerVodOk carries no videoDetails.channelId at all; externalChannelId is the only source here.
        Assert.Equal("channel:" + YouTubeFixtures.ChannelId, page!.Hero!.SubtitleEntityId);
        Assert.Contains("2026-08-20", page.Hero.MetaLine!, StringComparison.Ordinal);
        Assert.Contains("987,654 views", page.Hero.MetaLine!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_Video_NextIsAskedAsTheWebMetadataClient()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk);
        (ModuleTestHost host, _) = Make(http);

        await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);

        RecordedRequest next = Assert.Single(NextCalls(http));
        Assert.Equal("POST", next.Method);
        Assert.Contains("\"videoId\":\"" + Id + "\"", next.Body, StringComparison.Ordinal);
        Assert.Contains("\"clientName\":\"WEB\"", next.Body, StringComparison.Ordinal);
        Assert.Contains("\"hl\":\"en\"", next.Body, StringComparison.Ordinal);
        Assert.Equal("1", next.Header("X-YouTube-Client-Name"));
        Assert.Equal("https://www.youtube.com", next.Header("Origin"));
        Assert.Equal(YouTubeModule.ConsentCookie, next.Header("Cookie"));
        Assert.Equal(YouTubeModule.DesktopUserAgent, next.Header("User-Agent"));

        // /next plays nothing, so the playback members of the /player body have no business here.
        Assert.DoesNotContain("playbackContext", next.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("contentCheckOk", next.Body, StringComparison.Ordinal);
    }

    /// <summary>The WEB block is metadata-only: it must never enter the /player fallback walk, whose bans on WEB
    /// (SABR-only sessions needing the JS player) are unchanged.</summary>
    [Fact]
    public void ClientTable_KeepsWebOutOfThePlayerWalk()
    {
        var module = new YouTubeModule(new ScriptedHttpHandler(), disposeHandler: true);

        Assert.Equal(["visionos", "android", "ios"], module.Clients.Select(c => c.Key));
        Assert.All(module.Clients, c => Assert.True(c.IsPlayback));
        Assert.Equal("web", module.MetadataClient!.Key);
        Assert.Equal("WEB", module.MetadataClient.ClientName);
        Assert.True(module.MetadataClient.IsMetadata);
        Assert.Equal(4, module.ClientTable.Length);
    }

    [Fact]
    public async Task Page_Video_NeedsNoPlayableManifest()
    {
        // A SABR-only session cannot play, but the page is still worth showing — so no HLS url and no preflight.
        var http = Player(YouTubeFixtures.PlayerSabrOnly);
        (ModuleTestHost host, _) = Make(http);

        ModulePageDoc? page = await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal("Claude FM", page!.Hero!.Title);
        Assert.DoesNotContain(http.Requests, r => r.Url.Contains("manifest.googlevideo.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Page_Video_VodShowsItsLength()
    {
        var http = Player(YouTubeFixtures.PlayerVodOk, YouTubeFixtures.NextWatchVod);
        (ModuleTestHost host, _) = Make(http);

        ModulePageDoc? page = await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);

        Assert.False(page!.Hero!.IsLive);
        PageSection facts = page.Sections.Single(x => x.Kind == PageSection.KindFacts);
        Assert.Contains(facts.Rows!, r => r[0] == "Length" && r[1] == "1:01:12");
        Assert.Contains(facts.Rows!, r => r[0] == "Views" && r[1] == "987,654");
    }

    [Fact]
    public async Task Page_Video_BlockedOnEveryClientIsATypedFailure()
    {
        var http = Player(YouTubeFixtures.PlayerVideoIdMismatch);
        (ModuleTestHost host, _) = Make(http);

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(
            () => host.PageAsync("video:" + Id, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.Unavailable, ex.Code);
    }

    [Fact]
    public async Task Page_Channel_WithNothingResolvedYetIsHonestAboutIt()
    {
        (ModuleTestHost host, ScriptedHttpHandler http) = Make(new ScriptedHttpHandler());

        ModulePageDoc? page = await host.PageAsync("channel:UC123", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Empty(http.Requests);                                  // there is no channel endpoint to call
        Assert.False(page!.Hero!.IsLive);
        PageAction open = Assert.Single(page.Actions);
        Assert.Equal("https://www.youtube.com/channel/UC123", open.Url);
        PageSection note = Assert.Single(page.Sections);
        Assert.Equal(PageSection.KindText, note.Kind);
        Assert.Contains("player response", note.Text!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_Channel_ShowsLiveNowOnceTheChannelsBroadcastWasResolved()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk).WithManifest();
        (ModuleTestHost host, _) = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync(Id, TestContext.Current.CancellationToken);
        ModulePageDoc? page = await host.PageAsync(resolved.SubtitleEntityId!, TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal("Anthropic", page!.Hero!.Title);
        Assert.True(page.Hero.IsLive);
        PageSection live = Assert.Single(page.Sections);
        Assert.Equal(PageSection.KindPlayables, live.Kind);
        PageItem item = Assert.Single(live.Items!);
        Assert.Equal(Id, item.PlayableId);
        Assert.Equal("video:" + Id, item.EntityId);
        Assert.True(item.IsLive);
        Assert.Equal(MediaForm.Video, item.Form);
    }

    /// <summary>A channel page still makes ZERO http calls of its own: the avatar was cached by the video page that
    /// last mentioned this channel, so <c>/next</c> is paid for once and reused.</summary>
    [Fact]
    public async Task Page_Channel_ShowsTheAvatarAVideoPageCachedWithoutAnyRequestOfItsOwn()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk);
        (ModuleTestHost host, _) = Make(http);

        await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);
        int requestsAfterTheVideoPage = http.Requests.Count;

        ModulePageDoc? page = await host.PageAsync("channel:" + YouTubeFixtures.ChannelId,
            TestContext.Current.CancellationToken);

        Assert.Equal(requestsAfterTheVideoPage, http.Requests.Count);
        Assert.Equal("Anthropic", page!.Hero!.Title);
        Assert.Equal(YouTubeFixtures.ChannelAvatarUrl, page.Hero.AvatarUrl);
        Assert.True(page.Hero.IsLive);
        PageItem item = Assert.Single(page.Sections.Single(x => x.Kind == PageSection.KindPlayables).Items!);
        Assert.Equal(Id, item.PlayableId);
    }

    /// <summary>A resolve after a page visit must not erase the avatar that visit learned: the snapshot merges.</summary>
    [Fact]
    public async Task Page_Channel_KeepsTheAvatarWhenAResolveLaterOverwritesTheSnapshot()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk).WithManifest();
        (ModuleTestHost host, _) = Make(http);

        await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);
        await host.ResolveAsync(Id, TestContext.Current.CancellationToken);
        ModulePageDoc? page = await host.PageAsync("channel:" + YouTubeFixtures.ChannelId,
            TestContext.Current.CancellationToken);

        Assert.Equal(YouTubeFixtures.ChannelAvatarUrl, page!.Hero!.AvatarUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("station:http://x/y")]
    [InlineData("video:not-an-id")]
    [InlineData("channel:")]
    public async Task Page_ForeignOrMalformedEntityIdsAreNull(string entityId)
    {
        (ModuleTestHost host, _) = Make(new ScriptedHttpHandler());

        Assert.Null(await host.PageAsync(entityId, TestContext.Current.CancellationToken));
    }

    /// <summary>Playback latency must not pay for page copy: /next is a page-path call and nothing else. The rule
    /// IS scripted here, so this asserts the module chose not to call it rather than that it could not.</summary>
    [Fact]
    public async Task Resolve_NeverCallsTheWatchNextEndpoint()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk).WithManifest();
        (ModuleTestHost host, _) = Make(http);

        await host.ResolveAsync(Id, TestContext.Current.CancellationToken);

        Assert.Empty(NextCalls(http));
        Assert.DoesNotContain(http.Requests, r => r.Url.Contains("youtubei/v1/next", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resolve_CarriesThePageAndSubtitleEntityIds()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk).WithManifest();
        (ModuleTestHost host, _) = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync(Id, TestContext.Current.CancellationToken);

        Assert.Equal("video:" + Id, resolved.PageEntityId);
        Assert.Equal("channel:UCAAAAAAAAAAAAAAAAAAAAA", resolved.SubtitleEntityId);
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Answers every client's player request with the same body, and every <c>/next</c> request with a watch-next
    /// document. The <c>/next</c> rule is here rather than in the page tests alone because
    /// <see cref="ScriptedHttpHandler"/> throws on an unscripted request: a resolve test that never calls the
    /// endpoint proves it by the recorded requests, not by the transport blowing up.
    /// </summary>
    /// <param name="playerJson">The <c>/player</c> body every client gets.</param>
    /// <param name="nextJson">The <c>/next</c> body, defaulting to the live watch-next document.</param>
    private static ScriptedHttpHandler Player(string playerJson, string? nextJson = null)
        => new ScriptedHttpHandler()
            .OnUrl("youtubei/v1/next", HttpStatusCode.OK, nextJson ?? YouTubeFixtures.NextWatchLive,
                "application/json")
            .OnUrl("youtubei/v1/player", HttpStatusCode.OK, playerJson, "application/json");

    private static RecordedRequest[] PlayerCalls(ScriptedHttpHandler http)
        => http.Requests.Where(r => r.Url.Contains("youtubei/v1/player", StringComparison.Ordinal)).ToArray();

    private static RecordedRequest[] NextCalls(ScriptedHttpHandler http)
        => http.Requests.Where(r => r.Url.Contains("youtubei/v1/next", StringComparison.Ordinal)).ToArray();
}

/// <summary>
/// <see cref="YouTubeWallPolicy"/> and <see cref="YouTubeSessionStore"/> as pure values: no module, no transport, no
/// host. The classification used to live in two boolean predicates whose CORRECTNESS depended on the order the caller
/// happened to ask them in, so the point of these tests is that the order is now a value you can assert.
/// </summary>
public class YouTubeWallPolicyTests
{
    private const string Bot = "Sign in to confirm you're not a bot";

    [Theory]
    // Playable, and the two non-wall refusals.
    [InlineData("OK", null, false, 0, 1, 0, PlayabilityVerdict.Ok)]
    [InlineData("LIVE_STREAM_OFFLINE", "This live event will begin in a few moments.", false, 0, 1, 0,
        PlayabilityVerdict.Offline)]
    [InlineData("UNPLAYABLE", "This video is not available on this app.", false, 0, 1, 0,
        PlayabilityVerdict.Unplayable)]
    [InlineData(null, null, false, 0, 1, 0, PlayabilityVerdict.Unplayable)]
    // The wall, at both escalations.
    [InlineData("LOGIN_REQUIRED", Bot, false, 0, 1, 0, PlayabilityVerdict.BotWallRetryable)]
    [InlineData("LOGIN_REQUIRED", Bot, false, 1, 2, 0, PlayabilityVerdict.BotWallBlocked)]
    [InlineData("LOGIN_REQUIRED", Bot, false, 0, 1, 2, PlayabilityVerdict.BotWallBlocked)]
    // clientsTried 0 = describing a cooldown, no request made. The streak alone words it.
    [InlineData("LOGIN_REQUIRED", Bot, false, 0, 0, 0, PlayabilityVerdict.BotWallRetryable)]
    [InlineData("LOGIN_REQUIRED", Bot, false, 0, 0, 2, PlayabilityVerdict.BotWallBlocked)]
    // THE A5 CASE. A bare LOGIN_REQUIRED with neither age evidence nor the word "bot" was silently an AGE GATE —
    // terminal NeedsAuth, no next client — and only stayed off the real bot wall because the bot predicate happened
    // to be tested first. It is a wall.
    [InlineData("LOGIN_REQUIRED", "Sign in", false, 0, 1, 0, PlayabilityVerdict.BotWallRetryable)]
    [InlineData("LOGIN_REQUIRED", null, false, 0, 1, 0, PlayabilityVerdict.BotWallRetryable)]
    [InlineData("LOGIN_REQUIRED", "Please sign in to continue.", false, 0, 1, 0,
        PlayabilityVerdict.BotWallRetryable)]
    // The age gate, on each of its three positive markers — and NEVER on LOGIN_REQUIRED alone.
    [InlineData("LOGIN_REQUIRED", "Sign in to confirm your age", false, 0, 1, 0, PlayabilityVerdict.AgeGate)]
    [InlineData("LOGIN_REQUIRED", null, true, 0, 1, 0, PlayabilityVerdict.AgeGate)]
    [InlineData("AGE_CHECK_REQUIRED", null, false, 0, 1, 0, PlayabilityVerdict.AgeGate)]
    [InlineData("AGE_VERIFICATION_REQUIRED", null, false, 0, 1, 0, PlayabilityVerdict.AgeGate)]
    [InlineData("UNPLAYABLE", "This video may be inappropriate for some users.", false, 0, 1, 0,
        PlayabilityVerdict.AgeGate)]
    // The ordering, stated rather than incidental: the age MARKER outranks bot wording, and bot wording never
    // outranks an age marker. Both directions are asserted so a reorder cannot pass.
    [InlineData("LOGIN_REQUIRED", Bot, true, 0, 1, 0, PlayabilityVerdict.AgeGate)]
    [InlineData("LOGIN_REQUIRED", "confirm your age", false, 1, 2, 0, PlayabilityVerdict.AgeGate)]
    public void Classify_IsExplicitAboutTheOrderItAsksIn(string? status, string? reason, bool ageFlag,
        int clientsWalled, int clientsTried, long recentWalls, PlayabilityVerdict expected)
        => Assert.Equal(expected,
            YouTubeWallPolicy.Classify(status, reason, ageFlag, clientsWalled, clientsTried, recentWalls));

    [Theory]
    [InlineData(-3, 0L)]
    [InlineData(0, 0L)]
    [InlineData(1, 30_000L)]
    [InlineData(2, 120_000L)]
    [InlineData(3, 300_000L)]
    [InlineData(50, 300_000L)]
    public void CooldownMsFor_EscalatesThenCaps(int consecutiveWalls, long expected)
        => Assert.Equal(expected, YouTubeWallPolicy.CooldownMsFor(consecutiveWalls));

    /// <summary>The cooldown never runs away: even an absurd streak stays inside the ~38-minute window the
    /// 2026-08-23 session spent walled, so the module always comes back on its own.</summary>
    [Fact]
    public void CooldownMsFor_NeverExceedsFiveMinutes()
    {
        for (int walls = 0; walls < 100; walls++)
        {
            Assert.InRange(YouTubeWallPolicy.CooldownMsFor(walls), 0L, 300_000L);
        }
    }

    [Fact]
    public void SessionStore_RoundTripsEveryMember()
    {
        string dir = TempDir();
        try
        {
            var session = new YouTubeSession("CgtBQUFBQUFBQUFBQQ%3D%3D", "android", 1_767_225_600_000L);
            YouTubeSessionStore.Save(dir, session);

            Assert.Equal(session, YouTubeSessionStore.Load(dir));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>Save creates the directory it is given: a module's data dir need not exist yet on first run.</summary>
    [Fact]
    public void SessionStore_SaveCreatesTheDataDir()
    {
        string root = TempDir();
        string dir = System.IO.Path.Combine(root, "not-created-yet");
        try
        {
            YouTubeSessionStore.Save(dir, new YouTubeSession("v", "ios", 7));

            Assert.Equal(new YouTubeSession("v", "ios", 7), YouTubeSessionStore.Load(dir));
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>A session file is a cache of conveniences: nothing about it — missing, truncated, not JSON at all, or
    /// a path that is not a directory — is worth failing a play for, so every one of them is the empty session.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json at all")]
    [InlineData("[]")]
    [InlineData("{\"visitorData\": 17}")]
    public void SessionStore_LoadNeverThrowsAndDefaultsInstead(string? fileContent)
    {
        string dir = TempDir();
        try
        {
            if (fileContent is not null)
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(dir, YouTubeSessionStore.FileName), fileContent);
            }

            Assert.Equal(YouTubeSession.Empty, YouTubeSessionStore.Load(dir));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SessionStore_TreatsAnAbsentDataDirAsNoSession(string dataDir)
    {
        Assert.Equal(YouTubeSession.Empty, YouTubeSessionStore.Load(dataDir));
        YouTubeSessionStore.Save(dataDir, new YouTubeSession("v", "android", 1));   // must not throw
    }

    private static string TempDir()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wavee-yt-session",
            Guid.NewGuid().ToString("n"));
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
        catch (System.IO.IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Small script builders shared by the YouTube tests.</summary>
internal static class YouTubeScriptExtensions
{
    /// <summary>Answers the manifest preflight with a valid HLS master.</summary>
    /// <param name="http">The handler to extend.</param>
    public static ScriptedHttpHandler WithManifest(this ScriptedHttpHandler http)
        => http.OnUrl("manifest.googlevideo.com", HttpStatusCode.OK, YouTubeFixtures.HlsMaster,
            "application/vnd.apple.mpegurl");
}
