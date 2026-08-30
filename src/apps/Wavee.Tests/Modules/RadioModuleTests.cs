using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Wavee.Module.Radio;
using Wavee.Sdk;
using Wavee.Tests.Modules.Fixtures;
using Xunit;

namespace Wavee.Tests.Modules;

/// <summary>
/// The Radio module, driven through <see cref="ModuleTestHost"/> over a scripted transport: playlist unwrapping and
/// the ICY header probe, with no network anywhere.
/// </summary>
public class RadioModuleTests
{
    private const string StationUrl = "http://ice1.example.org:8000/stream1.mp3";

    private static ModuleTestHost Make(ScriptedHttpHandler http) => new(new RadioModule(http));

    // ---- match -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("http://ice1.example.org:8000/stream1.mp3")]
    [InlineData("https://ice1.example.org/stream")]
    [InlineData("https://example.org/station.pls")]
    public async Task Match_ClaimsAnyHttpUrlWithLowConfidence(string input)
    {
        ModuleTestHost host = Make(new ScriptedHttpHandler());

        MatchResult? match = await host.MatchAsync(input, TestContext.Current.CancellationToken);

        Assert.NotNull(match);
        Assert.Equal(MediaForm.Audio, match.Form);
        Assert.True(match.IsLive);
        Assert.True(match.Confidence < 0.5, "the fallback module must never outrank a real one");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("file:///c:/music/song.mp3")]
    [InlineData("spotify:track:abc")]
    public async Task Match_DeclinesEverythingThatIsNotHttp(string input)
    {
        ModuleTestHost host = Make(new ScriptedHttpHandler());

        Assert.Null(await host.MatchAsync(input, TestContext.Current.CancellationToken));
    }

    // ---- playlist parsing (pure) -------------------------------------------------------------------------------

    [Fact]
    public void Pls_TakesTheLowestNumberedEntry()
        => Assert.Equal(StationUrl, StreamPlaylist.FirstPlsEntry(RadioFixtures.Pls));

    [Fact]
    public void Pls_WithNoEntriesReturnsNull()
        => Assert.Null(StreamPlaylist.FirstPlsEntry(RadioFixtures.PlsEmpty));

    [Fact]
    public void M3u_TakesTheFirstNonCommentLine()
        => Assert.Equal(StationUrl, StreamPlaylist.FirstM3uEntry(RadioFixtures.M3u));

    [Fact]
    public void M3u_ResolvesRelativeEntriesAgainstTheirPlaylist()
        => Assert.Equal("http://ice1.example.org:8000/live/stream1.mp3",
            StreamPlaylist.FirstM3uEntry(RadioFixtures.M3uRelative, "http://ice1.example.org:8000/live/index.m3u"));

    [Theory]
    [InlineData(nameof(RadioFixtures.Pls), StreamPlaylist.Kind.Pls)]
    [InlineData(nameof(RadioFixtures.M3u), StreamPlaylist.Kind.M3u)]
    [InlineData(nameof(RadioFixtures.M3uBare), StreamPlaylist.Kind.M3u)]
    [InlineData(nameof(RadioFixtures.Hls), StreamPlaylist.Kind.Hls)]
    public void Classify_SeparatesStationPlaylistsFromHls(string fixture, StreamPlaylist.Kind expected)
    {
        string body = fixture switch
        {
            nameof(RadioFixtures.Pls) => RadioFixtures.Pls,
            nameof(RadioFixtures.M3u) => RadioFixtures.M3u,
            nameof(RadioFixtures.M3uBare) => RadioFixtures.M3uBare,
            _ => RadioFixtures.Hls,
        };

        Assert.Equal(expected, StreamPlaylist.Classify(body, null));
    }

    // ---- resolve -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Resolve_UnwrapsAPlsAndProbesTheStream()
    {
        var http = new ScriptedHttpHandler()
            .OnUrl("station.pls", HttpStatusCode.OK, RadioFixtures.Pls, "audio/x-scpls")
            .On(r => r.Url == StationUrl, _ => ScriptedHttpHandler.Respond(HttpStatusCode.OK, "", "audio/mpeg",
            [
                ("icy-name", "Example Radio"),
                ("icy-genre", "Ambient"),
                ("icy-br", "128"),
                ("icy-metaint", "16000"),
                ("icy-description", "The example station"),
            ]));
        ModuleTestHost host = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync("https://example.org/station.pls",
            TestContext.Current.CancellationToken);

        Assert.Equal(StationUrl, resolved.Media.Url);
        Assert.Equal(MediaLocator.ContainerIcy, resolved.Media.Container);
        Assert.Equal("audio/mpeg", resolved.Media.ContentType);
        Assert.Equal(MediaForm.Audio, resolved.Form);
        Assert.True(resolved.IsLive);
        Assert.Equal(0, resolved.DurationMs);
        Assert.Equal("Example Radio", resolved.Title);
        Assert.Equal(new[] { "Ambient" }, resolved.Artists);
        Assert.Equal("1", http.Requests[1].Header("Icy-MetaData"));
    }

    [Fact]
    public async Task Resolve_UnwrapsAnM3u()
    {
        var http = new ScriptedHttpHandler()
            .OnUrl("station.m3u", HttpStatusCode.OK, RadioFixtures.M3u, "audio/x-mpegurl")
            .On(r => r.Url == StationUrl, _ => ScriptedHttpHandler.Respond(HttpStatusCode.OK, "", "audio/mpeg",
                [("icy-metaint", "8192")]));
        ModuleTestHost host = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync("https://example.org/station.m3u",
            TestContext.Current.CancellationToken);

        Assert.Equal(StationUrl, resolved.Media.Url);
        Assert.Equal(MediaLocator.ContainerIcy, resolved.Media.Container);
    }

    [Fact]
    public async Task Resolve_KeepsAnHlsManifestAsItIs()
    {
        var http = new ScriptedHttpHandler()
            .OnUrl("live.m3u8", HttpStatusCode.OK, RadioFixtures.Hls, "application/vnd.apple.mpegurl");
        ModuleTestHost host = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync("https://example.org/live.m3u8",
            TestContext.Current.CancellationToken);

        Assert.Equal("https://example.org/live.m3u8", resolved.Media.Url);
        Assert.Equal(MediaLocator.ContainerHls, resolved.Media.Container);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Resolve_ProbesABareStreamUrlDirectly()
    {
        var http = new ScriptedHttpHandler()
            .On(_ => true, _ => ScriptedHttpHandler.Respond(HttpStatusCode.OK, "", "audio/aacp",
            [
                ("icy-name", "AAC Example"),
                ("icy-br", "64"),
                ("icy-metaint", "16000"),
            ]));
        ModuleTestHost host = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync("http://ice1.example.org:8000/aac",
            TestContext.Current.CancellationToken);

        Assert.Single(http.Requests);
        Assert.Equal("audio/aacp", resolved.Media.ContentType);
        Assert.Equal(MediaLocator.ContainerIcy, resolved.Media.Container);
        Assert.Equal("AAC Example", resolved.Title);
    }

    [Fact]
    public async Task Resolve_FiniteContentLengthIsProgressiveAndNotLive()
    {
        var http = new ScriptedHttpHandler()
            .On(_ => true, _ => ScriptedHttpHandler.Respond(HttpStatusCode.OK, "", "audio/mpeg",
                headers: null, contentLength: 4_200_000));
        ModuleTestHost host = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync("https://example.org/podcast-episode.mp3",
            TestContext.Current.CancellationToken);

        Assert.Equal(MediaLocator.ContainerProgressive, resolved.Media.Container);
        Assert.False(resolved.IsLive);
    }

    [Fact]
    public async Task Resolve_NoIcyHeadersFallsBackToTheHostAsTheTitle()
    {
        var http = new ScriptedHttpHandler()
            .On(_ => true, _ => ScriptedHttpHandler.Respond(HttpStatusCode.OK, "", "audio/ogg"));
        ModuleTestHost host = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync("http://ice1.example.org:8000/stream.ogg",
            TestContext.Current.CancellationToken);

        Assert.Equal("ice1.example.org", resolved.Title);
        Assert.Empty(resolved.Artists);
        Assert.Equal(MediaLocator.ContainerIcy, resolved.Media.Container);
    }

    [Fact]
    public async Task Resolve_ARejectedIcyStatusLineStillResolvesAsAnIcyStream()
    {
        // SocketsHttpHandler throws on the "ICY 200 OK" status line SHOUTcast v1 sends; the app's own live
        // transport speaks HTTP/1.0 and copes, so the module must not turn this into a failure.
        var http = new ScriptedHttpHandler()
            .On(_ => true, _ => throw new HttpRequestException("The server returned an invalid or unrecognized response."));
        ModuleTestHost host = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync("http://ice1.example.org:8000/shoutcast",
            TestContext.Current.CancellationToken);

        Assert.Equal(MediaLocator.ContainerIcy, resolved.Media.Container);
        Assert.True(resolved.IsLive);
        Assert.Equal("ice1.example.org", resolved.Title);
    }

    [Fact]
    public async Task Resolve_AnEmptyPlaylistIsUnavailable()
    {
        var http = new ScriptedHttpHandler()
            .OnUrl("empty.pls", HttpStatusCode.OK, RadioFixtures.PlsEmpty, "audio/x-scpls");
        ModuleTestHost host = Make(http);

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync("https://example.org/empty.pls", TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.Unavailable, ex.Code);
    }

    [Fact]
    public async Task Resolve_A404IsOffline()
    {
        var http = new ScriptedHttpHandler().On(_ => true,
            _ => ScriptedHttpHandler.Respond(HttpStatusCode.NotFound, "gone"));
        ModuleTestHost host = Make(http);

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync("http://ice1.example.org:8000/dead", TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.Offline, ex.Code);
    }

    [Fact]
    public async Task Resolve_StopsAfterTooManyPlaylistHops()
    {
        var http = new ScriptedHttpHandler()
            .On(_ => true, _ => ScriptedHttpHandler.Respond(HttpStatusCode.OK,
                "[playlist]\nnumberofentries=1\nFile1=https://example.org/next.pls\n", "audio/x-scpls"));
        ModuleTestHost host = Make(http);

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync("https://example.org/first.pls", TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.Unavailable, ex.Code);
        Assert.Equal(RadioModule.MaxPlaylistHops + 1, http.Requests.Count);
    }

    [Fact]
    public async Task Resolve_RejectsANonHttpPlayableId()
    {
        ModuleTestHost host = Make(new ScriptedHttpHandler());

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync("spotify:track:abc", TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.NotOwned, ex.Code);
    }

    // ---- header parsing (pure) ---------------------------------------------------------------------------------

    [Fact]
    public void IcyInfo_ReadsEveryHeaderItCaresAbout()
    {
        var response = ScriptedHttpHandler.Respond(HttpStatusCode.OK, "", "audio/mpeg",
        [
            ("icy-name", " Example Radio "),
            ("icy-genre", "Ambient"),
            ("icy-br", "128"),
            ("icy-metaint", "16000"),
            ("icy-url", "https://example.org"),
            ("icy-pub", "1"),
        ]);

        IcyInfo icy = IcyInfo.FromHeaders(
            System.Linq.Enumerable.Concat(response.Headers, response.Content.Headers),
            response.Content.Headers.ContentType?.ToString(), response.Content.Headers.ContentLength);

        Assert.Equal("Example Radio", icy.Name);
        Assert.Equal("Ambient", icy.Genre);
        Assert.Equal(128, icy.BitrateKbps);
        Assert.Equal(16000, icy.MetaInt);
        Assert.Equal("https://example.org", icy.Url);
        Assert.Equal("audio/mpeg", icy.ContentType);
        Assert.Equal(MediaLocator.ContainerIcy, icy.Container);
    }

    [Fact]
    public void IcyInfo_UnknownIsAnIcyStream()
        => Assert.Equal(MediaLocator.ContainerIcy, IcyInfo.Unknown.Container);

    // ---- pages -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Page_Station_IsBuiltFromTheIcyHeaders()
    {
        var http = new ScriptedHttpHandler()
            .On(_ => true, _ => ScriptedHttpHandler.Respond(HttpStatusCode.OK, "", "audio/mpeg",
            [
                ("icy-name", "Example Radio"),
                ("icy-genre", "Ambient"),
                ("icy-description", "Handpicked ambient, around the clock."),
                ("icy-url", "https://example.org"),
                ("icy-br", "128"),
                ("icy-metaint", "16000"),
            ]));
        ModuleTestHost host = Make(http);

        ModulePageDoc? page = await host.PageAsync(
            RadioModule.StationEntityPrefix + StationUrl, TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal("Example Radio", page!.Hero!.Title);
        Assert.Equal("Radio station", page.Hero.Eyebrow);
        Assert.Equal("Ambient", page.Hero.Subtitle);
        Assert.True(page.Hero.IsLive);
        Assert.Contains("128 kbit/s", page.Hero.MetaLine!, StringComparison.Ordinal);

        PageAction play = Assert.Single(page.Actions);
        Assert.Equal(PageAction.KindPlay, play.Kind);
        Assert.Equal(StationUrl, play.PlayableId);

        PageSection facts = page.Sections.Single(x => x.Kind == PageSection.KindFacts);
        Assert.Contains(facts.Rows!, r => r[0] == "Bitrate" && r[1] == "128 kbit/s");
        Assert.Contains(facts.Rows!, r => r[0] == "Genre" && r[1] == "Ambient");
        Assert.Contains(facts.Rows!, r => r[0] == "Format" && r[1] == "audio/mpeg");

        Assert.Contains("Handpicked ambient",
            page.Sections.Single(x => x.Kind == PageSection.KindText).Text!, StringComparison.Ordinal);

        PageItem link = page.Sections.Single(x => x.Kind == PageSection.KindLinks).Items!.Single();
        Assert.Equal("https://example.org", link.Url);

        // ICY titles are interleaved in the AUDIO body, which only the app demuxes - the module must not pretend.
        Assert.DoesNotContain(page.Sections, x => x.Kind == PageSection.KindPlayables);
    }

    [Fact]
    public async Task Page_Station_UnwrapsAPlaylistExactlyLikeResolveDoes()
    {
        var http = new ScriptedHttpHandler()
            .OnUrl("station.pls", HttpStatusCode.OK, RadioFixtures.Pls, "audio/x-scpls")
            .On(_ => true, _ => ScriptedHttpHandler.Respond(HttpStatusCode.OK, "", "audio/mpeg",
                [("icy-name", "Example Radio")]));
        ModuleTestHost host = Make(http);

        ModulePageDoc? page = await host.PageAsync(
            RadioModule.StationEntityPrefix + "https://example.org/station.pls",
            TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal("Example Radio", page!.Hero!.Title);
        Assert.Equal(StationUrl, Assert.Single(page.Actions).PlayableId);
    }

    [Fact]
    public async Task Page_Station_WithoutAWebsiteHasNoLinksSection()
    {
        var http = new ScriptedHttpHandler()
            .On(_ => true, _ => ScriptedHttpHandler.Respond(HttpStatusCode.OK, "", "audio/mpeg",
                [("icy-name", "Example Radio"), ("icy-url", "not a url")]));
        ModuleTestHost host = Make(http);

        ModulePageDoc? page = await host.PageAsync(
            RadioModule.StationEntityPrefix + StationUrl, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(page!.Sections, x => x.Kind == PageSection.KindLinks);
    }

    [Theory]
    [InlineData("")]
    [InlineData("video:tRsQsTMvPNg")]
    [InlineData("station:")]
    [InlineData("station:not a url")]
    public async Task Page_ForeignOrMalformedEntityIdsAreNull(string entityId)
    {
        ModuleTestHost host = Make(new ScriptedHttpHandler());

        Assert.Null(await host.PageAsync(entityId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Resolve_PointsBothLinkSlotsAtTheStationsFinalUrl()
    {
        var http = new ScriptedHttpHandler()
            .OnUrl("station.pls", HttpStatusCode.OK, RadioFixtures.Pls, "audio/x-scpls")
            .On(_ => true, _ => ScriptedHttpHandler.Respond(HttpStatusCode.OK, "", "audio/mpeg",
                [("icy-name", "Example Radio")]));
        ModuleTestHost host = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync("https://example.org/station.pls",
            TestContext.Current.CancellationToken);

        Assert.Equal(RadioModule.StationEntityPrefix + StationUrl, resolved.PageEntityId);
        Assert.Equal(RadioModule.StationEntityPrefix + StationUrl, resolved.SubtitleEntityId);
    }
}
