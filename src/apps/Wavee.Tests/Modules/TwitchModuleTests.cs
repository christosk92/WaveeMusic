using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Wavee.Module.Twitch;
using Wavee.Sdk;
using Wavee.Tests.Modules.Fixtures;
using Xunit;

namespace Wavee.Tests.Modules;

/// <summary>
/// The Twitch module, driven through <see cref="ModuleTestHost"/> over a scripted transport. Every GQL envelope,
/// token document and usher body is a fixture; nothing here touches the network.
/// </summary>
public class TwitchModuleTests
{
    private const string Login = TwitchFixtures.Login;
    private const int Slot = 1234567;

    private static ModuleTestHost Make(ScriptedHttpHandler http)
        => new(new TwitchModule(http, disposeHandler: false, playerSlot: () => Slot));

    // ---- match -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://www.twitch.tv/examplestreamer", "live:examplestreamer")]
    [InlineData("https://twitch.tv/ExampleStreamer", "live:examplestreamer")]
    [InlineData("https://m.twitch.tv/examplestreamer", "live:examplestreamer")]
    [InlineData("https://go.twitch.tv/examplestreamer", "live:examplestreamer")]
    [InlineData("twitch.tv/examplestreamer", "live:examplestreamer")]
    [InlineData("https://player.twitch.tv/?channel=examplestreamer&parent=twitch.tv", "live:examplestreamer")]
    [InlineData("https://www.twitch.tv/videos/1234567890", "vod:1234567890")]
    [InlineData("https://www.twitch.tv/examplestreamer/v/1234567890", "vod:1234567890")]
    [InlineData("https://www.twitch.tv/examplestreamer/video/1234567890", "vod:1234567890")]
    [InlineData("https://player.twitch.tv/?video=v1234567890", "vod:1234567890")]
    [InlineData("https://www.twitch.tv/examplestreamer/schedule?vodID=1234567890", "vod:1234567890")]
    public async Task Match_MapsTheUrlTable(string input, string expected)
    {
        ModuleTestHost host = Make(new ScriptedHttpHandler());

        MatchResult? match = await host.MatchAsync(input, TestContext.Current.CancellationToken);

        Assert.NotNull(match);
        Assert.Equal(expected, match.PlayableId);
        Assert.Equal(MediaForm.Video, match.Form);
        Assert.Equal(expected.StartsWith("live:", StringComparison.Ordinal), match.IsLive);
    }

    [Theory]
    [InlineData("https://clips.twitch.tv/SomeFunnyClipSlug")]
    [InlineData("https://www.twitch.tv/examplestreamer/clip/SomeFunnyClipSlug")]
    [InlineData("https://www.twitch.tv/directory")]
    [InlineData("https://www.twitch.tv/")]
    [InlineData("https://www.youtube.com/watch?v=tRsQsTMvPNg")]
    [InlineData("https://example.org/stream.mp3")]
    public async Task Match_DeclinesClipsAndForeignLinks(string input)
    {
        ModuleTestHost host = Make(new ScriptedHttpHandler());

        Assert.Null(await host.MatchAsync(input, TestContext.Current.CancellationToken));
    }

    // ---- usher url construction --------------------------------------------------------------------------------

    [Fact]
    public void UsherLiveUrl_UsesTheV2EndpointWithTheRightParameters()
    {
        string url = TwitchModule.UsherLiveUrl(Login, "sig-value", "{\"a\":1}", Slot, legacy: false);

        Assert.StartsWith($"https://usher.ttvnw.net/api/v2/channel/hls/{Login}.m3u8?", url, StringComparison.Ordinal);
        Assert.Contains("sig=sig-value", url, StringComparison.Ordinal);
        Assert.Contains("token=%7B%22a%22%3A1%7D", url, StringComparison.Ordinal);
        Assert.Contains("&allow_source=true", url, StringComparison.Ordinal);
        Assert.Contains("&allow_audio_only=true", url, StringComparison.Ordinal);
        Assert.Contains("&playlist_include_framerate=true", url, StringComparison.Ordinal);
        Assert.Contains("&supported_codecs=h264", url, StringComparison.Ordinal);
        Assert.Contains("&platform=web", url, StringComparison.Ordinal);
        Assert.Contains($"&p={Slot}", url, StringComparison.Ordinal);

        // Low-latency prefetch tags and non-h264 renditions are exactly what Media Foundation cannot read.
        Assert.DoesNotContain("fast_bread", url, StringComparison.Ordinal);
        Assert.DoesNotContain("h265", url, StringComparison.Ordinal);
        Assert.DoesNotContain("av1", url, StringComparison.Ordinal);
    }

    [Fact]
    public void UsherLiveUrl_LegacyDropsTheV2Segment()
    {
        string url = TwitchModule.UsherLiveUrl(Login, "s", "t", Slot, legacy: true);

        Assert.StartsWith($"https://usher.ttvnw.net/api/channel/hls/{Login}.m3u8?", url, StringComparison.Ordinal);
        Assert.Contains("&supported_codecs=h264", url, StringComparison.Ordinal);
    }

    [Fact]
    public void UsherVodUrl_UsesNauthOnV2AndSigOnLegacy()
    {
        string v2 = TwitchModule.UsherVodUrl("1234567890", "s", "t", Slot, legacy: false);
        Assert.StartsWith("https://usher.ttvnw.net/vod/v2/1234567890.m3u8?nauthsig=s&nauth=t", v2,
            StringComparison.Ordinal);

        string legacy = TwitchModule.UsherVodUrl("1234567890", "s", "t", Slot, legacy: true);
        Assert.StartsWith("https://usher.ttvnw.net/vod/1234567890.m3u8?sig=s&token=t", legacy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_DefaultPlayerSlotIsSevenDigits()
    {
        var http = new ScriptedHttpHandler()
            .OnBody("streamPlaybackAccessToken(channelName", HttpStatusCode.OK, TwitchFixtures.LiveTokenEnvelope())
            .OnBody("StreamMetadata", HttpStatusCode.OK, TwitchFixtures.StreamMetadataLive)
            .OnUrl("usher.ttvnw.net", HttpStatusCode.OK, TwitchFixtures.UsherMasterV2);
        var host = new ModuleTestHost(new TwitchModule(http));

        ResolvedPlayable resolved = await host.ResolveAsync("live:" + Login, TestContext.Current.CancellationToken);

        Assert.Matches(@"&p=\d{7}$", resolved.Media.Url!);
    }

    // ---- resolve -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Resolve_UsesTheInlineQueryFirstAndBuildsTheUsherUrl()
    {
        var http = new ScriptedHttpHandler()
            .OnBody("streamPlaybackAccessToken(channelName", HttpStatusCode.OK, TwitchFixtures.LiveTokenEnvelope())
            .OnBody("StreamMetadata", HttpStatusCode.OK, TwitchFixtures.StreamMetadataLive)
            .OnUrl("usher.ttvnw.net", HttpStatusCode.OK, TwitchFixtures.UsherMasterV2,
                "application/vnd.apple.mpegurl");
        ModuleTestHost host = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync("live:" + Login, TestContext.Current.CancellationToken);

        RecordedRequest gql = http.Requests[0];
        Assert.Equal("https://gql.twitch.tv/gql", gql.Url);
        Assert.Equal(TwitchModule.ClientId, gql.Header("Client-ID"));
        Assert.Equal(TwitchModule.DesktopUserAgent, gql.Header("User-Agent"));
        Assert.Null(gql.Header("Authorization"));
        Assert.Null(gql.Header("Device-Id"));
        Assert.Null(gql.Header("Client-Integrity"));
        Assert.DoesNotContain("persistedQuery", gql.Body, StringComparison.Ordinal);

        Assert.Contains("usher.ttvnw.net/api/v2/channel/hls/", resolved.Media.Url!, StringComparison.Ordinal);
        Assert.Equal(MediaLocator.ContainerHls, resolved.Media.Container);
        Assert.Equal(MediaForm.Video, resolved.Form);
        Assert.True(resolved.IsLive);
        Assert.Equal(0, resolved.DurationMs);
        Assert.Equal(1_767_225_600L * 1000L, resolved.ExpiresAtUnixMs!.Value);
    }

    [Fact]
    public async Task Resolve_FallsBackToThePersistedHashOnPersistedQueryNotFound()
    {
        var http = new ScriptedHttpHandler()
            .OnBody("\"query\"", HttpStatusCode.OK, TwitchFixtures.PersistedQueryNotFound)
            .OnBody(TwitchModule.PlaybackAccessTokenHash, HttpStatusCode.OK, TwitchFixtures.LiveTokenEnvelope())
            .OnBody("StreamMetadata", HttpStatusCode.OK, TwitchFixtures.StreamMetadataLive)
            .OnUrl("usher.ttvnw.net", HttpStatusCode.OK, TwitchFixtures.UsherMasterV2);
        ModuleTestHost host = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync("live:" + Login, TestContext.Current.CancellationToken);

        RecordedRequest persisted = http.Requests[1];
        Assert.Contains("\"operationName\":\"PlaybackAccessToken\"", persisted.Body, StringComparison.Ordinal);
        Assert.Contains(TwitchModule.PlaybackAccessTokenHash, persisted.Body, StringComparison.Ordinal);
        Assert.Contains("\"playerType\":\"embed\"", persisted.Body, StringComparison.Ordinal);
        Assert.Contains("\"isLive\":true", persisted.Body, StringComparison.Ordinal);
        Assert.NotNull(resolved.Media.Url);
    }

    [Fact]
    public async Task Resolve_NullTokenDataIsUnavailable()
    {
        var http = new ScriptedHttpHandler()
            .OnUrl("gql.twitch.tv", HttpStatusCode.OK, TwitchFixtures.NullTokenEnvelope);
        ModuleTestHost host = Make(http);

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync("live:" + Login, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.Unavailable, ex.Code);
        Assert.Contains("requires a browser", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, http.Requests.Count);   // inline, then the persisted retry
    }

    [Fact]
    public async Task Resolve_ForbiddenAuthorizationShowsTheReason()
    {
        var http = new ScriptedHttpHandler()
            .OnUrl("gql.twitch.tv", HttpStatusCode.OK,
                TwitchFixtures.LiveTokenEnvelope(TwitchFixtures.TokenValueForbidden));
        ModuleTestHost host = Make(http);

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync("live:" + Login, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.Unavailable, ex.Code);
        Assert.Equal("This channel is temporarily unavailable.", ex.Message);
    }

    [Fact]
    public async Task Resolve_GeoblockReasonIsGeoBlocked()
    {
        var http = new ScriptedHttpHandler()
            .OnUrl("gql.twitch.tv", HttpStatusCode.OK,
                TwitchFixtures.LiveTokenEnvelope(TwitchFixtures.TokenValueGeoblocked));
        ModuleTestHost host = Make(http);

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync("live:" + Login, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.GeoBlocked, ex.Code);
        Assert.Equal("blocked in your country", ex.Detail);
    }

    [Fact]
    public async Task Resolve_RestrictedBitratesOnlyWarns()
    {
        var http = new ScriptedHttpHandler()
            .OnBody("streamPlaybackAccessToken(channelName", HttpStatusCode.OK,
                TwitchFixtures.LiveTokenEnvelope(TwitchFixtures.TokenValueRestrictedBitrates))
            .OnBody("StreamMetadata", HttpStatusCode.OK, TwitchFixtures.StreamMetadataLive)
            .OnUrl("usher.ttvnw.net", HttpStatusCode.OK, TwitchFixtures.UsherMasterV2);
        ModuleTestHost host = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync("live:" + Login, TestContext.Current.CancellationToken);

        Assert.NotNull(resolved.Media.Url);
        Assert.Contains(host.Logs, l => l.Level == ModuleLogLevel.Warn &&
                                        l.Message.Contains("subscriber-only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resolve_FallsBackToTheLegacyUsherEndpointOn4xx()
    {
        var http = new ScriptedHttpHandler()
            .OnBody("streamPlaybackAccessToken(channelName", HttpStatusCode.OK, TwitchFixtures.LiveTokenEnvelope())
            .OnBody("StreamMetadata", HttpStatusCode.OK, TwitchFixtures.StreamMetadataLive)
            .OnUrl("/api/v2/channel/hls/", HttpStatusCode.BadRequest, "")
            .OnUrl("/api/channel/hls/", HttpStatusCode.OK, TwitchFixtures.UsherMasterV2);
        ModuleTestHost host = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync("live:" + Login, TestContext.Current.CancellationToken);

        Assert.Contains("usher.ttvnw.net/api/channel/hls/", resolved.Media.Url!, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/v2/", resolved.Media.Url!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_SubscriberOnlyManifestIsNeedsAuth()
    {
        var http = new ScriptedHttpHandler()
            .OnBody("videoPlaybackAccessToken(id", HttpStatusCode.OK, TwitchFixtures.VodTokenEnvelope())
            .OnUrl("usher.ttvnw.net", HttpStatusCode.Forbidden, TwitchFixtures.UsherRestricted,
                "application/json");
        ModuleTestHost host = Make(http);

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync("vod:1234567890", TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.NeedsAuth, ex.Code);
        Assert.Equal("vod_manifest_restricted", ex.Detail);
    }

    [Fact]
    public async Task Resolve_UsherFailureWithNoStreamIsOffline()
    {
        var http = new ScriptedHttpHandler()
            .OnBody("streamPlaybackAccessToken(channelName", HttpStatusCode.OK, TwitchFixtures.LiveTokenEnvelope())
            .OnBody("StreamMetadata", HttpStatusCode.OK, TwitchFixtures.StreamMetadataOffline)
            .OnUrl("usher.ttvnw.net", HttpStatusCode.NotFound, TwitchFixtures.UsherTransoceanic, "application/json");
        ModuleTestHost host = Make(http);

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync("live:" + Login, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.Offline, ex.Code);
    }

    [Fact]
    public async Task Resolve_ReadsTitleArtistAndArtworkFromStreamMetadata()
    {
        var http = new ScriptedHttpHandler()
            .OnBody("streamPlaybackAccessToken(channelName", HttpStatusCode.OK, TwitchFixtures.LiveTokenEnvelope())
            .OnBody("StreamMetadata", HttpStatusCode.OK, TwitchFixtures.StreamMetadataLive)
            .OnUrl("usher.ttvnw.net", HttpStatusCode.OK, TwitchFixtures.UsherMasterV2);
        ModuleTestHost host = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync("live:" + Login, TestContext.Current.CancellationToken);

        Assert.Equal("Building a Rust parser", resolved.Title);
        Assert.Equal(new[] { "ExampleStreamer" }, resolved.Artists);
        Assert.Equal(
            "https://static-cdn.jtvnw.net/previews-ttv/live_user_examplestreamer-1920x1080.jpg",
            resolved.ArtworkUrl);

        RecordedRequest metadata = http.Requests.Single(r =>
            r.Body.Contains("StreamMetadata", StringComparison.Ordinal));
        Assert.Contains(TwitchModule.StreamMetadataHash, metadata.Body, StringComparison.Ordinal);
        Assert.Contains("\"includeIsDJ\":true", metadata.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_RejectsAnIdThatIsNotATwitchPlayable()
    {
        ModuleTestHost host = Make(new ScriptedHttpHandler());

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync("https://example.org/x", TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.NotOwned, ex.Code);
    }

    [Fact]
    public void ParseUsherError_ReadsTheCodeOutOfTheArrayBody()
    {
        (string? error, string? code) = TwitchModule.ParseUsherError(TwitchFixtures.UsherRestricted);

        Assert.Equal("Manifest is restricted", error);
        Assert.Equal("vod_manifest_restricted", code);
    }

    [Fact]
    public void ParseUsherError_IgnoresAPlaylistBody()
        => Assert.Equal((null, null), TwitchModule.ParseUsherError(TwitchFixtures.UsherMasterV2));

    // ---- pages -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Page_Channel_Live_ComesStraightOutOfStreamMetadata()
    {
        var http = new ScriptedHttpHandler()
            .OnUrl("gql.twitch.tv", HttpStatusCode.OK, TwitchFixtures.StreamMetadataLive);
        ModuleTestHost host = Make(http);

        ModulePageDoc? page = await host.PageAsync("channel:" + Login, TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        // A watch page's title is WHAT IS ON and its channel row is WHO, which is the inverse of Twitch's own model
        // (channel as the entity, stream title as one of its properties). The live arm swaps them so the caption does
        // not name the wrong thing twice — a title reading "ExampleStreamer" over a channel row reading "Building a
        // Rust parser". Offline keeps Twitch's own order, because there the channel IS the subject.
        Assert.Equal("Building a Rust parser", page!.Hero!.Title);
        Assert.Equal("ExampleStreamer", page.Hero.Subtitle);
        Assert.Equal("Live stream", page.Hero.Eyebrow);

        // A live channel is a WATCH page: the stream preview is the stage's poster (the hero's own art) and the
        // channel's face moves to the avatar slot, instead of the two fighting over one image field.
        Assert.Equal(ModulePageDoc.TemplateWatch, page.Template);
        Assert.Equal("https://static-cdn.jtvnw.net/previews-ttv/live_user_examplestreamer-1920x1080.jpg",
            page.Hero.ImageUrl);
        Assert.Equal("https://static-cdn.jtvnw.net/user-default-pictures/300x300.png", page.Hero.AvatarUrl);

        // On Twitch the thing and its owner are the same entity, so the subtitle has nowhere else to go.
        Assert.Null(page.Hero.SubtitleEntityId);

        Assert.True(page.Hero.IsLive);
        Assert.Contains("1,234 watching", page.Hero.MetaLine!, StringComparison.Ordinal);

        PageAction play = page.Actions.Single(a => a.Kind == PageAction.KindPlay);
        Assert.Equal("live:" + Login, play.PlayableId);
        Assert.True(play.Primary);
        Assert.Equal("https://www.twitch.tv/" + Login,
            page.Actions.Single(a => a.Kind == PageAction.KindOpenUrl).Url);

        PageSection facts = page.Sections.Single(x => x.Kind == PageSection.KindFacts);
        Assert.Contains(facts.Rows!, r => r[0] == "Category" && r[1] == "Science & Technology");
        Assert.Contains(facts.Rows!, r => r[0] == "Viewers" && r[1] == "1,234");
        Assert.Contains(facts.Rows!, r => r[0] == "Status" && r[1] == "Live");

        // The persisted StreamMetadata query is what the page rides on.
        Assert.Contains(TwitchModule.StreamMetadataHash, http.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_Channel_Offline_OffersNoPlayButton()
    {
        var http = new ScriptedHttpHandler()
            .OnUrl("gql.twitch.tv", HttpStatusCode.OK, TwitchFixtures.StreamMetadataOffline);
        ModuleTestHost host = Make(http);

        ModulePageDoc? page = await host.PageAsync("channel:" + Login, TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.False(page!.Hero!.IsLive);
        PageAction only = Assert.Single(page.Actions);
        Assert.Equal(PageAction.KindOpenUrl, only.Kind);
        Assert.Contains(page.Sections, x => x.Kind == PageSection.KindText &&
            x.Text!.Contains("not live right now", StringComparison.Ordinal));

        // An offline channel has no picture to stage, so it stays the entity layout it has always been: the avatar
        // is the hero's art, there is no separate avatar slot, and there is nothing to navigate the subtitle to.
        Assert.Equal(ModulePageDoc.TemplateEntity, page.Template);
        Assert.Null(page.Hero.Subtitle);
        Assert.Null(page.Hero.AvatarUrl);
        Assert.Null(page.Hero.SubtitleEntityId);
        Assert.Contains(page.Sections, x => x.Kind == PageSection.KindFacts &&
            Array.Exists(x.Rows!, r => r[0] == "Status" && r[1] == "Offline"));
    }

    [Fact]
    public async Task Page_Channel_Live_SubstitutesThePreviewSizePlaceholders()
    {
        var http = new ScriptedHttpHandler()
            .OnUrl("gql.twitch.tv", HttpStatusCode.OK, TwitchFixtures.StreamMetadataLiveTemplatedPreview);
        ModuleTestHost host = Make(http);

        ModulePageDoc? page = await host.PageAsync("channel:" + Login, TestContext.Current.CancellationToken);

        // Braces are not valid in a url path: unsubstituted, the watch stage would poster nothing at all.
        Assert.NotNull(page);
        Assert.Equal(ModulePageDoc.TemplateWatch, page!.Template);
        Assert.Equal(
            $"https://static-cdn.jtvnw.net/previews-ttv/live_user_{Login}-" +
            $"{TwitchModule.PreviewWidth}x{TwitchModule.PreviewHeight}.jpg",
            page.Hero!.ImageUrl);
        Assert.DoesNotContain("{", page.Hero.ImageUrl!, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewImage_LeavesAnAlreadySizedUrlAloneAndPassesNullThrough()
    {
        const string sized = "https://static-cdn.jtvnw.net/previews-ttv/live_user_x-640x360.jpg";

        Assert.Equal(sized, TwitchModule.PreviewImage(sized));
        Assert.Null(TwitchModule.PreviewImage(null));
        Assert.Null(TwitchModule.PreviewImage("   "));
    }

    /// <summary>The persisted <c>StreamMetadata</c> query often answers with no <c>previewImageURL</c> member at all —
    /// verified against a live channel — and before the login-derived fallback the watch stage then postered the 70x70
    /// channel avatar across a full-width 16:9 box. The url is a convention built from the login, exactly like the
    /// channel and usher urls, so it asserts nothing the module had not already learned.</summary>
    [Fact]
    public void PreviewFor_BuildsTheCanonicalLivePreviewPathAtStageSize()
    {
        Assert.Equal("https://static-cdn.jtvnw.net/previews-ttv/live_user_shroud-1920x1080.jpg",
            TwitchModule.PreviewFor("shroud"));
    }

    /// <summary>A live channel whose metadata carries no preview must still stage a real picture, and it must NOT be
    /// the avatar — the avatar belongs in the channel-row circle, where 70x70 is the right size.</summary>
    [Fact]
    public async Task Page_Channel_Live_WithoutAPreview_StagesTheCanonicalPreviewNotTheAvatar()
    {
        var http = new ScriptedHttpHandler()
            .OnUrl("gql.twitch.tv", HttpStatusCode.OK, TwitchFixtures.StreamMetadataLiveNoPreview);
        ModuleTestHost host = Make(http);

        ModulePageDoc? page = await host.PageAsync("channel:examplestreamer");

        Assert.NotNull(page);
        Assert.Equal(ModulePageDoc.TemplateWatch, page!.Template);
        Assert.Equal(TwitchModule.PreviewFor("examplestreamer"), page.Hero!.ImageUrl);
        Assert.NotEqual(page.Hero.ImageUrl, page.Hero.AvatarUrl);
        Assert.Contains("profile_image", page.Hero.AvatarUrl!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_Channel_ThatDoesNotExistIsNull()
    {
        var http = new ScriptedHttpHandler()
            .OnUrl("gql.twitch.tv", HttpStatusCode.OK, TwitchFixtures.StreamMetadataNoUser);
        ModuleTestHost host = Make(http);

        Assert.Null(await host.PageAsync("channel:" + Login, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("video:tRsQsTMvPNg")]
    [InlineData("channel:")]
    [InlineData("channel:not a login")]
    public async Task Page_ForeignOrMalformedEntityIdsAreNull(string entityId)
    {
        ModuleTestHost host = Make(new ScriptedHttpHandler());

        Assert.Null(await host.PageAsync(entityId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Resolve_Live_PointsBothLinkSlotsAtTheChannel()
    {
        var http = new ScriptedHttpHandler()
            .OnUrl("gql.twitch.tv", HttpStatusCode.OK, TwitchFixtures.LiveTokenEnvelope())
            .OnUrl("usher.ttvnw.net", HttpStatusCode.OK, TwitchFixtures.UsherMasterV2);
        ModuleTestHost host = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync("live:" + Login, TestContext.Current.CancellationToken);

        Assert.Equal("channel:" + Login, resolved.PageEntityId);
        Assert.Equal("channel:" + Login, resolved.SubtitleEntityId);
    }

    [Fact]
    public async Task Resolve_Vod_TakesItsChannelFromTheTokenDocument()
    {
        var http = new ScriptedHttpHandler()
            .OnUrl("gql.twitch.tv", HttpStatusCode.OK, TwitchFixtures.VodTokenEnvelope())
            .OnUrl("usher.ttvnw.net", HttpStatusCode.OK, TwitchFixtures.UsherMasterV2);
        ModuleTestHost host = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync("vod:1234567890", TestContext.Current.CancellationToken);

        Assert.Equal("channel:" + Login, resolved.PageEntityId);
        Assert.Equal("channel:" + Login, resolved.SubtitleEntityId);
    }
}
