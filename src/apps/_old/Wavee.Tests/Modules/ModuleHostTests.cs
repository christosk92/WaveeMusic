using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Backend.MediaSources;
using Wavee.Backend.Modules;
using Wavee.Core;
using Wavee.Sdk;
using MediaForm = Wavee.Sdk.MediaForm;
using Xunit;

namespace Wavee.Tests.Modules;

/// <summary>Match → resolve → Track, the resolve cache and its dedupe, and the routing through MediaProviderRegistry.</summary>
public class ModuleHostTests
{
    static readonly System.Threading.CancellationToken Ct = TestContext.Current.CancellationToken;

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
    public async Task MatchAsync_ResolvesAndBuildsTheTrack()
    {
        var script = YouTubeLike();
        (ModuleHost host, _) = ModuleFixtures.HostOver(script,
            ModuleFixtures.Manifest("wavee.youtube", urlPatterns: ["youtube.com"]));
        using (host)
        {
            ModuleMatch? match = await host.MatchAsync("https://www.youtube.com/watch?v=abc", null, Ct);

            Assert.NotNull(match);
            Assert.Equal("wavee.youtube", match.Module.Id);
            Assert.Equal("vid123", match.Match.PlayableId);
            Assert.Equal(MediaForm.Video, match.Resolved.Form);
            // The Track is the module's own uri namespace, streamed, playable, and duration 0 (live has no end).
            Assert.Equal(ModuleUri.Encode("wavee.youtube", "vid123"), match.Track.Uri);
            Assert.Equal("Claude FM", match.Track.Title);
            Assert.Equal(TrackOrigin.Streamed, match.Track.Origin);
            Assert.Equal(Availability.Playable, match.Track.Availability);
            Assert.Equal("module:wavee.youtube", match.Track.Source);
            Assert.Equal(0, match.Track.DurationMs);
            Assert.Equal("A channel", Assert.Single(match.Track.Artists).Name);
        }
    }

    [Fact]
    public async Task MatchAsync_UnclaimedInput_AnswersNull()
    {
        (ModuleHost host, _) = ModuleFixtures.HostOver(YouTubeLike(),
            ModuleFixtures.Manifest("wavee.youtube", urlPatterns: ["youtube.com"]));
        using (host)
        {
            Assert.Null(await host.MatchAsync("https://example.test/whatever", null, Ct));
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
        var script = new FakeModule();   // no Resolve hook: every attempt is a typed Unavailable
        (ModuleHost host, _) = ModuleFixtures.HostOver(script);
        using (host)
        {
            string uri = ModuleUri.Encode("wavee.fake", "p1");
            await Assert.ThrowsAsync<ModuleException>(() => host.ResolveAsync(uri, force: false, Ct));

            // The second attempt must reach the module again — a failed in-flight task must never be latched.
            script.Resolve = p => ModuleFixtures.Resolved(p.PlayableId);
            ResolvedPlayable ok = await host.ResolveAsync(uri, force: false, Ct);
            Assert.Equal("p1", ok.PlayableId);
        }
    }

    [Fact]
    public async Task AnUninstalledModuleUri_DoesNotLatchAFailedResolve()
    {
        (ModuleHost host, _) = ModuleFixtures.HostOver(new FakeModule());
        using (host)
        {
            string uri = ModuleUri.Encode("wavee.absent", "p1");
            for (int i = 0; i < 3; i++)
            {
                var ex = await Assert.ThrowsAsync<ModuleException>(() => host.ResolveAsync(uri, force: false, Ct));
                Assert.Equal(ModuleErrorCode.NotOwned, ex.Code);
            }
        }
    }

    [Fact]
    public async Task ResolveAsync_AForeignUri_IsNotOwned()
    {
        (ModuleHost host, _) = ModuleFixtures.HostOver(new FakeModule());
        using (host)
        {
            var ex = await Assert.ThrowsAsync<ModuleException>(
                () => host.ResolveAsync("spotify:track:abc", force: false, Ct));
            Assert.Equal(ModuleErrorCode.NotOwned, ex.Code);
        }
    }

    [Fact]
    public async Task Prefs_ReachTheModuleOnEveryResolve()
    {
        var script = YouTubeLike(MediaForm.Audio, isLive: false);
        (ModuleHost host, _) = ModuleFixtures.HostOver(script, null,
            prefs: () => new ResolvePreferences("veryHigh", true, 6000));
        using (host)
        {
            await host.ResolveAsync(ModuleUri.Encode("wavee.fake", "p1"), force: false, Ct);
            Assert.Equal("veryHigh", script.LastPrefs?.Quality);
            Assert.True(script.LastPrefs?.Metered);
            Assert.Equal(6000, script.LastPrefs?.CrossfadeMs);
        }
    }

    // ── routing through the registry ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Registry_RoutesAModuleUriToItsProvider_AndProducesTheRightHandle()
    {
        var script = YouTubeLike(MediaForm.Audio, isLive: false);
        (ModuleHost host, _) = ModuleFixtures.HostOver(script);
        using (host)
        {
            var registry = new MediaProviderRegistry(
                new LocalFileMediaProvider(_ => true),
                new GenericMediaProvider(_ => true),
                host.Providers[0]);

            string uri = ModuleUri.Encode("wavee.fake", "p1");
            Assert.Equal("wavee.fake", registry.OwnerOf(uri)?.Id);

            var track = LocalPlayables.ForModule("wavee.fake", "p1", "t", MediaForm.Audio);
            FastStartPlan plan = await registry.ResolveFastAsync(track, Ct);
            AudioStreamHandle body = await plan.Body;

            Assert.Equal(AudioSourceKind.ExternalPlain, body.SourceKind);
            Assert.Equal("https://cdn.test/a.mp3", body.CdnUrl);
            Assert.Equal(AudioFormat.Mp3, body.Format);
            Assert.Equal(42_000, body.DurationMs);
            Assert.Equal(0, plan.Start.HeadBytes.Length);   // the empty-head shape the external path already uses
        }
    }

    [Fact]
    public async Task VideoFormPlayable_IsRefusedByTheAudioPath()
    {
        (ModuleHost host, _) = ModuleFixtures.HostOver(YouTubeLike());
        using (host)
        {
            var registry = new MediaProviderRegistry(host.Providers[0]);
            var track = LocalPlayables.ForModule("wavee.fake", "p1", "t", MediaForm.Video);
            var ex = await Assert.ThrowsAsync<AudioPlaybackException>(() => registry.ResolveFastAsync(track, Ct));
            Assert.Equal(AudioKeyFailureReason.Restricted, ex.Reason);
        }
    }

    [Theory]
    [InlineData(true, MediaLocator.ContainerProgressive, AudioSourceKind.LiveStream)]
    [InlineData(false, MediaLocator.ContainerIcy, AudioSourceKind.LiveStream)]
    [InlineData(false, MediaLocator.ContainerProgressive, AudioSourceKind.ExternalPlain)]
    public void HandleFor_MapsLivenessAndContainerOntoTheSourceKind(bool isLive, string container, AudioSourceKind expected)
    {
        var resolved = ModuleFixtures.Resolved(isLive: isLive, durationMs: 5000,
            media: MediaLocator.FromUrl("https://x.test/a", container, "audio/mpeg"));
        AudioStreamHandle handle = ModuleMediaProvider.HandleFor("wavee:module:x:y", resolved);

        Assert.Equal(expected, handle.SourceKind);
        // A live body carries duration 0 — that is what keeps every ending-soon / gapless arm switched off.
        Assert.Equal(expected == AudioSourceKind.LiveStream ? 0 : 5000, handle.DurationMs);
    }

    [Fact]
    public void HandleFor_MapsAStreamLocatorOntoModuleStream()
    {
        var resolved = ModuleFixtures.Resolved(media: MediaLocator.FromStream("fileid-hex", "audio/ogg"));
        AudioStreamHandle handle = ModuleMediaProvider.HandleFor("wavee:module:x:y", resolved);

        Assert.Equal(AudioSourceKind.ModuleStream, handle.SourceKind);
        Assert.Equal("fileid-hex", handle.CdnUrl);
        Assert.Equal(AudioFormat.OggVorbis320, handle.Format);
    }

    [Theory]
    [InlineData("audio/aac", AudioFormat.Aac)]
    [InlineData("audio/aacp", AudioFormat.Aac)]
    [InlineData("audio/flac", AudioFormat.Flac)]
    [InlineData("audio/ogg", AudioFormat.OggVorbis320)]
    [InlineData("audio/mpeg", AudioFormat.Mp3)]
    [InlineData(null, AudioFormat.Mp3)]
    public void FormatOf_MapsTheContentType(string? contentType, AudioFormat expected)
        => Assert.Equal(expected, ModuleMediaProvider.FormatOf(contentType));

    [Fact]
    public void CapsOf_MapsTheDeclaredTokens()
    {
        Assert.Equal(MediaProviderCaps.None, ModuleMediaProvider.CapsOf(null));
        Assert.Equal(MediaProviderCaps.None, ModuleMediaProvider.CapsOf(["nonsense"]));
        Assert.Equal(MediaProviderCaps.PreparedNext | MediaProviderCaps.WireMeta,
            ModuleMediaProvider.CapsOf(["preparedNext", "wireMeta"]));
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
    public void ModulePlayables_AnswersFalseWithNothingAttached()
    {
        ModulePlayables.Attach(null);
        Assert.False(ModulePlayables.HasVideo("wavee:module:x:y"));
        Assert.False(ModulePlayables.IsLive("wavee:module:x:y"));
        Assert.Null(ModulePlayables.Get("wavee:module:x:y"));
    }
}
