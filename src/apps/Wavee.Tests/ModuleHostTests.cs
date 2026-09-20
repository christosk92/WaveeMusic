// Match → resolve, the resolve cache and its dedupe, the prefs a module sees, and the pure maps from a resolve answer
// onto the app's audio shapes and video source — ported from 0.2.9 Wavee.Tests/Modules/ModuleHostTests.cs. 0.3 has no
// provider registry, so the registry facts became the `AudioShapeOf` / `OpenedFor` / `VideoSourceOf` values the
// `Playback.Audio.ModuleOpen` seam and the video tier act on (G-021, G-148).

using Wavee.Sdk;
using Xunit;
using static Wavee.Modules;

namespace Wavee.Tests;

[Collection(ModuleStaticsCollection.Name)]
public class ModuleHostTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static FakeModule YouTubeLike(MediaForm form = MediaForm.Video, bool isLive = true)
    {
        var script = new FakeModule
        {
            Match = p => p.Input.Contains("youtube", StringComparison.OrdinalIgnoreCase)
                ? new MatchResult("vid123", "raw title", form, isLive, 0.9)
                : throw new ModuleException(ModuleErrorCode.NotOwned, "not mine"),
        };
        script.Resolve = p => ModuleFixtures.Resolved(p.PlayableId, form: form, isLive: isLive,
            media: form == MediaForm.Video
                ? MediaLocator.FromUrl("https://cdn.test/master.m3u8", MediaLocator.ContainerHls)
                : MediaLocator.FromUrl("https://cdn.test/a.mp3", MediaLocator.ContainerProgressive, "audio/mpeg"),
            durationMs: isLive ? 0 : 42_000, title: "Claude FM", artists: ["A channel"]);
        return script;
    }

    [Fact]
    public async Task MatchAsync_ResolvesTheFirstClaim()
    {
        (ModuleHost host, _) = ModuleFixtures.HostOver(YouTubeLike(), ModuleFixtures.Manifest("wavee.youtube", urlPatterns: ["youtube.com"]));
        using (host)
        {
            ModuleMatch? match = await host.MatchAsync("  https://www.youtube.com/watch?v=abc  ", null, Ct);

            Assert.NotNull(match);
            Assert.Equal("wavee.youtube", match.Module.Id);
            Assert.Equal("vid123", match.Match.PlayableId);
            Assert.Equal(MediaForm.Video, match.Resolved.Form);
            Assert.Equal("Claude FM", match.Resolved.Title);
            // The answer is cached under the module's own uri namespace — what the row, the stage and the links read.
            Assert.NotNull(host.Playables.Get(ModuleUri.Encode("wavee.youtube", "vid123")));
        }
    }

    [Fact]
    public async Task MatchAsync_UnclaimedInput_AnswersNull()
    {
        (ModuleHost host, _) = ModuleFixtures.HostOver(YouTubeLike(), ModuleFixtures.Manifest("wavee.youtube", urlPatterns: ["youtube.com"]));
        using (host)
        {
            Assert.Null(await host.MatchAsync("https://example.test/whatever", null, Ct));
            Assert.Null(await host.MatchAsync("   ", null, Ct));
        }
    }

    [Fact]
    public async Task MatchAsync_AFailedResolveThrowsTheModulesOwnWords()
    {
        var script = new FakeModule
        {
            Match = _ => new MatchResult("gone", null, MediaForm.Audio, false, 1),
            Resolve = _ => throw new ModuleException(ModuleErrorCode.Unavailable, "This video is private."),
        };
        (ModuleHost host, _) = ModuleFixtures.HostOver(script);
        using (host)
        {
            var ex = await Assert.ThrowsAsync<ModuleException>(() => host.MatchAsync("https://x.test/gone", null, Ct));
            Assert.Equal("This video is private.", ex.Message);
        }
    }

    [Fact]
    public async Task ResolveAsync_CachesAndDedupes()
    {
        var script = YouTubeLike(MediaForm.Audio, isLive: false);
        (ModuleHost host, _) = ModuleFixtures.HostOver(script);
        using (host)
        {
            string uri = ModuleUri.Encode("wavee.fake", "p1");
            Task<ResolvedPlayable> a = host.ResolveAsync(uri, force: false, Ct);
            Task<ResolvedPlayable> b = host.ResolveAsync(uri, force: false, Ct);
            await Task.WhenAll(a, b);
            await host.ResolveAsync(uri, force: false, Ct);

            Assert.Equal(1, script.ResolveCalls);
            Assert.NotNull(host.Playables.Get(uri));
        }
    }

    [Fact]
    public async Task ResolveAsync_Force_IgnoresTheCache()
    {
        var script = YouTubeLike(MediaForm.Audio, isLive: false);
        (ModuleHost host, _) = ModuleFixtures.HostOver(script);
        using (host)
        {
            string uri = ModuleUri.Encode("wavee.fake", "p1");
            await host.ResolveAsync(uri, force: false, Ct);
            await host.ResolveAsync(uri, force: true, Ct);
            Assert.Equal(2, script.ResolveCalls);
        }
    }

    [Fact]
    public async Task AFailedResolve_IsNotCachedAsAFailure()
    {
        var script = new FakeModule();
        (ModuleHost host, _) = ModuleFixtures.HostOver(script);
        using (host)
        {
            string uri = ModuleUri.Encode("wavee.fake", "p1");
            await Assert.ThrowsAsync<ModuleException>(() => host.ResolveAsync(uri, force: false, Ct));

            script.Resolve = p => ModuleFixtures.Resolved(p.PlayableId);
            ResolvedPlayable ok = await host.ResolveAsync(uri, force: false, Ct);
            Assert.Equal("p1", ok.PlayableId);
        }
    }

    [Fact]
    public async Task AnUninstalledModuleUri_IsNotOwned_EveryTime()
    {
        (ModuleHost host, _) = ModuleFixtures.HostOver(new FakeModule());
        using (host)
        {
            string uri = ModuleUri.Encode("wavee.absent", "p1");
            for (int i = 0; i < 3; i++)
                Assert.Equal(ModuleErrorCode.NotOwned, (await Assert.ThrowsAsync<ModuleException>(() => host.ResolveAsync(uri, force: false, Ct))).Code);
        }
    }

    [Fact]
    public async Task ResolveAsync_AForeignUri_IsNotOwned()
    {
        (ModuleHost host, _) = ModuleFixtures.HostOver(new FakeModule());
        using (host)
        {
            var ex = await Assert.ThrowsAsync<ModuleException>(() => host.ResolveAsync("spotify:track:abc", force: false, Ct));
            Assert.Equal(ModuleErrorCode.NotOwned, ex.Code);
        }
    }

    [Fact]
    public async Task Prefs_ReachTheModuleOnEveryResolve()
    {
        var script = YouTubeLike(MediaForm.Audio, isLive: false);
        (ModuleHost host, _) = ModuleFixtures.HostOver(script, null, prefs: () => new ResolvePreferences("veryHigh", true, 6000));
        using (host)
        {
            await host.ResolveAsync(ModuleUri.Encode("wavee.fake", "p1"), force: false, Ct);
            Assert.Equal("veryHigh", script.LastPrefs?.Quality);
            Assert.True(script.LastPrefs?.Metered);
            Assert.Equal(6000, script.LastPrefs?.CrossfadeMs);
        }
    }

    [Theory]
    [InlineData(0, false, false, 5000, "normal", 0)]
    [InlineData(1, false, true, 5000, "high", 5000)]
    [InlineData(2, true, true, 30_000, "veryHigh", 12_000)]
    [InlineData(3, false, true, -4, "lossless", 0)]
    public void PrefsFrom_IsSourceNeutral(int quality, bool metered, bool crossfade, int crossfadeMs, string expectedQuality, int expectedCrossfade)
    {
        ResolvePreferences p = PrefsFrom(quality, metered, crossfade, crossfadeMs);
        Assert.Equal(expectedQuality, p.Quality);
        Assert.Equal(metered, p.Metered);
        Assert.Equal(expectedCrossfade, p.CrossfadeMs);
    }

    [Fact]
    public async Task Metadata_MergesIntoTheCachedAnswer_AndIsRaisedWithTheUri()
    {
        var script = YouTubeLike(MediaForm.Audio, isLive: true);
        (ModuleHost host, Func<FakeModuleChannel?> channel) = ModuleFixtures.HostOver(script);
        using (host)
        {
            string uri = ModuleUri.Encode("wavee.fake", "p1");
            await host.ResolveAsync(uri, force: false, Ct);
            var raised = new TaskCompletionSource<(string, MetadataUpdate)>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.MetadataChanged += (u, m) => raised.TrySetResult((u, m));

            channel()!.Module.Notify(Wavee.Sdk.Protocol.ModuleMethods.Metadata, new MetadataUpdate("p1", "Now: A Song", null, null),
                Wavee.Sdk.Protocol.SdkJsonContext.Default.MetadataUpdate);

            (string raisedUri, MetadataUpdate update) = await raised.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.Equal(uri, raisedUri);
            Assert.Equal("Now: A Song", update.Title);
            Assert.Equal("Now: A Song", host.Playables.Get(uri)!.Title);
            Assert.Equal(["A channel"], host.Playables.Get(uri)!.Artists);   // a null field leaves the cached one alone
        }
    }

    // ── the audio shapes (0.2.9 ModuleMediaProvider.HandleFor) ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, MediaLocator.ContainerProgressive, AudioShape.Live)]
    [InlineData(false, MediaLocator.ContainerIcy, AudioShape.Live)]
    [InlineData(false, MediaLocator.ContainerProgressive, AudioShape.Progressive)]
    [InlineData(false, MediaLocator.ContainerHls, AudioShape.None)]
    public void AudioShapeOf_MapsLivenessAndContainer(bool isLive, string container, AudioShape expected)
        => Assert.Equal(expected, AudioShapeOf(ModuleFixtures.Resolved(isLive: isLive, media: MediaLocator.FromUrl("https://x.test/a", container, "audio/mpeg"))));

    [Fact]
    public void AudioShapeOf_AStreamLocatorIsModuleServed_AndVideoIsRefused()
    {
        Assert.Equal(AudioShape.ModuleStream, AudioShapeOf(ModuleFixtures.Resolved(media: MediaLocator.FromStream("fileid-hex", "audio/ogg"))));
        Assert.Equal(AudioShape.None, AudioShapeOf(ModuleFixtures.Resolved(media: new MediaLocator(MediaLocator.KindStream, null, null, null, null, ""))));
        Assert.Equal(AudioShape.Video, AudioShapeOf(ModuleFixtures.Resolved(form: MediaForm.Video)));
        Assert.Equal(AudioShape.None, AudioShapeOf(ModuleFixtures.Resolved(media: MediaLocator.FromUrl("file:///C:/x.mp3"))));
        Assert.Equal(AudioShape.None, AudioShapeOf(null));
    }

    [Fact]
    public void OpenedFor_ALiveBodyDeclaresNoDuration()
    {
        // Duration 0 is what keeps every ending-soon / gapless / prepared-next arm switched off for a live body.
        Playback.Audio.Opened live = OpenedFor(ModuleFixtures.Resolved(isLive: true, durationMs: 5000,
            media: MediaLocator.FromUrl("https://x.test/a", MediaLocator.ContainerIcy, "audio/mpeg")), null);
        Assert.True(live.IsLive);
        Assert.Equal(0, live.DurationMs);
        Assert.Equal(Spotify.Audio.Format.Mp3, live.Format);

        Playback.Audio.Opened finite = OpenedFor(ModuleFixtures.Resolved(durationMs: 5000), null);
        Assert.False(finite.IsLive);
        Assert.Equal(5000, finite.DurationMs);
    }

    [Fact]
    public void OpenedFor_TheStreamsOwnContentTypeWins()
    {
        var resolved = ModuleFixtures.Resolved(media: MediaLocator.FromStream("s", "audio/mpeg"));
        Assert.Equal(Spotify.Audio.Format.OggVorbis320, OpenedFor(resolved, "audio/ogg").Format);
        Assert.Equal(Spotify.Audio.Format.Mp3, OpenedFor(resolved, null).Format);
        Assert.Equal(Spotify.Audio.Format.Flac, OpenedFor(resolved, "audio/flac").Format);
    }

    [Theory]
    [InlineData("audio/aac", true)]
    [InlineData("audio/aacp", true)]
    [InlineData("audio/mp4; codecs=mp4a.40.2", false)]
    [InlineData("audio/mpeg", false)]
    [InlineData(null, false)]
    public void LooksAac_ReadsTheContentType(string? contentType, bool expected) => Assert.Equal(expected, LooksAac(contentType, default));

    // ── the video tier (G-148) ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void VideoSourceOf_AModuleVideoUrlIsAClearSource_CarryingLiveness()
    {
        var source = VideoSourceOf(ModuleFixtures.Resolved(form: MediaForm.Video, isLive: true,
            media: MediaLocator.FromUrl("https://usher.test/master.m3u8", MediaLocator.ContainerHls)));
        Assert.NotNull(source);
        Assert.Equal("https://usher.test/master.m3u8", source.ClearUrl);
        Assert.True(source.IsLive);
    }

    [Fact]
    public void VideoSourceOf_RefusesAudio_AStreamLocator_AndANonWebUrl()
    {
        Assert.Null(VideoSourceOf(ModuleFixtures.Resolved(form: MediaForm.Audio)));
        Assert.Null(VideoSourceOf(ModuleFixtures.Resolved(form: MediaForm.Video, media: MediaLocator.FromStream("s"))));
        Assert.Null(VideoSourceOf(ModuleFixtures.Resolved(form: MediaForm.Video, media: MediaLocator.FromUrl("file:///C:/v.mp4"))));
        Assert.Null(VideoSourceOf(null));
    }

    // ── the resolve cache's sync answers ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Cache_ExpiresEntriesAtTheModulesOwnDeadline()
    {
        long now = 1_000;
        var cache = new ModulePlayableCache(() => now);
        const string uri = "wavee:module:x:y";
        cache.Put(uri, ModuleFixtures.Resolved(form: MediaForm.Video, isLive: true, expiresAtUnixMs: 2_000));

        Assert.True(cache.HasVideo(uri));
        Assert.True(cache.IsLive(uri));

        now = 2_000;
        Assert.Null(cache.Get(uri));
        Assert.False(cache.HasVideo(uri));
        Assert.False(cache.IsLive(uri));
        Assert.True(cache.IsExpired(uri));
        Assert.NotNull(cache.GetIncludingExpired(uri));
    }

    [Fact]
    public void Cache_InvalidateModule_DropsOnlyThatModulesEntries()
    {
        var cache = new ModulePlayableCache();
        cache.Put(ModuleUri.Encode("wavee.a", "1"), ModuleFixtures.Resolved());
        cache.Put(ModuleUri.Encode("wavee.b", "1"), ModuleFixtures.Resolved());

        cache.InvalidateModule("wavee.a");

        Assert.Null(cache.Get(ModuleUri.Encode("wavee.a", "1")));
        Assert.NotNull(cache.Get(ModuleUri.Encode("wavee.b", "1")));
    }

    [Fact]
    public void Playables_AnswersFalseWithNothingAttached()
    {
        Playables.Attach(null);
        Assert.False(Playables.HasVideo("wavee:module:x:y"));
        Assert.False(Playables.IsLive("wavee:module:x:y"));
        Assert.Null(Playables.Get("wavee:module:x:y"));
    }
}

/// <summary>The module facades (<c>Modules.Playables</c>, <c>Modules.Pages</c>, <c>Modules.Current</c>) are
/// process-wide statics: every test that attaches one runs in this collection, serially.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ModuleStaticsCollection
{
    public const string Name = "Module statics";
}
