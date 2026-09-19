using System;
using System.Collections.Generic;
using Wavee;
using Wavee.Backend.Modules;
using Wavee.Core;
using Wavee.Sdk;
using Xunit;

namespace Wavee.Tests.Modules;

/// <summary>
/// The module-PAGE cache and the route algebra behind it (Part 9.2). Three things are pinned here, and they are the
/// three that a rendering test could never see:
/// <list type="bullet">
///   <item>a document lives exactly as long as the module said, and the default is ten minutes — an expired entry
///   reads as ABSENT rather than as stale truth;</item>
///   <item>the route family is <c>module:</c> + the module uri, and it round-trips — a pin, a tab and a history row
///   all carry that one string, so a parse and a build that disagreed would be silent;</item>
///   <item>a link with nowhere to go is NULL, never a route key that paints as the "Your Library" fallback.</item>
/// </list>
/// </summary>
public class ModulePagesTests
{
    const string ModuleId = "wavee.youtube";

    static ModulePageDoc Doc(long? expiresAt = null, params PageSection[] sections)
        => new(ModulePageDoc.CurrentVersion, ModulePageDoc.TemplateEntity,
               new PageHero("Claude FM", "Channel", "Anthropic", null, "Live", true),
               [], sections, expiresAt);

    // ── the cache ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void APageIsReadableUntilTheModulesOwnExpiry()
    {
        long now = 1_000_000;
        var cache = new ModulePageCache(() => now);
        string uri = ModuleUri.Encode(ModuleId, "video:abc");

        cache.Put(uri, Doc(expiresAt: now + 5_000));
        Assert.NotNull(cache.Get(uri));
        Assert.False(cache.IsExpired(uri));

        now += 4_999;
        Assert.NotNull(cache.Get(uri));

        now += 1;                       // exactly AT the expiry instant — already gone
        Assert.Null(cache.Get(uri));
        Assert.True(cache.IsExpired(uri));
    }

    [Fact]
    public void APageWithNoStatedExpiry_LivesForTheDefaultWindow()
    {
        long now = 0;
        var cache = new ModulePageCache(() => now);
        string uri = ModuleUri.Encode(ModuleId, "channel:UC1");

        cache.Put(uri, Doc(expiresAt: null));
        Assert.Equal(10 * 60 * 1000, ModulePageCache.DefaultTtlMs);

        now = ModulePageCache.DefaultTtlMs - 1;
        Assert.NotNull(cache.Get(uri));

        now = ModulePageCache.DefaultTtlMs;
        Assert.Null(cache.Get(uri));
    }

    [Fact]
    public void ZeroOrNegativeExpiry_IsTreatedAsUnstated_NotAsAlreadyDead()
    {
        long now = 50_000;
        var cache = new ModulePageCache(() => now);
        string uri = ModuleUri.Encode(ModuleId, "video:zero");

        // A module that writes 0 means "I have no expiry", not "expire me in 1970" — the same reading
        // ModulePlayableCache gives the field.
        cache.Put(uri, Doc(expiresAt: 0));
        Assert.NotNull(cache.Get(uri));
    }

    [Fact]
    public void InvalidateDropsOnePage_InvalidateModuleDropsAllOfThem()
    {
        var cache = new ModulePageCache(() => 0);
        string a = ModuleUri.Encode(ModuleId, "video:a");
        string b = ModuleUri.Encode(ModuleId, "video:b");
        string other = ModuleUri.Encode("wavee.radio", "station:x");
        cache.Put(a, Doc());
        cache.Put(b, Doc());
        cache.Put(other, Doc());

        cache.Invalidate(a);
        Assert.Null(cache.Get(a));
        Assert.NotNull(cache.Get(b));

        cache.InvalidateModule(ModuleId);
        Assert.Null(cache.Get(b));
        Assert.NotNull(cache.Get(other));   // a crash in one module never blanks another's pages
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void AnEmptyUri_IsNeitherStoredNorFound()
    {
        var cache = new ModulePageCache(() => 0);
        cache.Put("", Doc());
        Assert.Equal(0, cache.Count);
        Assert.Null(cache.Get(""));
        Assert.Null(cache.Get(null));
        Assert.False(cache.IsExpired(null));
    }

    // ── the route algebra ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheRouteIsTheFamilyPrefixInFrontOfTheModuleUri()
    {
        string route = ModulePages.RouteForEntity(ModuleId, "channel:UC_x-y")!;
        Assert.Equal("module:" + ModuleUri.Encode(ModuleId, "channel:UC_x-y"), route);
        Assert.StartsWith("module:", route, StringComparison.Ordinal);
        Assert.True(ModulePages.IsRoute(route));
    }

    [Fact]
    public void ARouteRoundTripsBackToItsTwoHalves()
    {
        // An entity id may contain colons and slashes (a radio station id IS a url); the base64url payload is what
        // keeps the route splittable anyway.
        const string entity = "station:https://stream.example.com/live?x=1";
        string route = ModulePages.RouteForEntity(ModuleId, entity)!;

        Assert.True(ModulePages.TryParseRoute(route, out string moduleId, out string entityId));
        Assert.Equal(ModuleId, moduleId);
        Assert.Equal(entity, entityId);
        Assert.Equal(ModuleUri.Encode(ModuleId, entity), ModulePages.UriOf(route));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("module:")]
    [InlineData("module:not-a-module-uri")]
    [InlineData("album:spotify:album:1")]
    [InlineData("home")]
    public void AnythingThatIsNotAModuleRoute_ParsesAsNothing(string? key)
    {
        Assert.False(ModulePages.TryParseRoute(key, out _, out _));
        if (key != "module:not-a-module-uri") Assert.Null(ModulePages.UriOf(key));
    }

    [Theory]
    [InlineData(null, "video:a")]
    [InlineData("", "video:a")]
    [InlineData(ModuleId, null)]
    [InlineData(ModuleId, "")]
    public void AHalfMissingLink_IsInertRatherThanADeadRoute(string? moduleId, string? entityId)
        => Assert.Null(ModulePages.RouteForEntity(moduleId, entityId));

    // ── the sync identity lookups ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheIdentityClusterReadsTheModulesTwoEntityIds()
    {
        var playables = new ModulePlayableCache(() => 0);
        ModulePlayables.Attach(playables);
        try
        {
            string uri = ModuleUri.Encode(ModuleId, "video:abc");
            playables.Put(uri, Resolved(pageEntityId: "video:abc", subtitleEntityId: "channel:UC1"));

            Assert.Equal(ModulePages.RouteForEntity(ModuleId, "video:abc"), ModulePages.RouteFor(uri, LinkSlot.Title));
            Assert.Equal(ModulePages.RouteForEntity(ModuleId, "video:abc"), ModulePages.RouteFor(uri, LinkSlot.Art));
            Assert.Equal(ModulePages.RouteForEntity(ModuleId, "channel:UC1"), ModulePages.RouteFor(uri, LinkSlot.Artist));
        }
        finally { ModulePlayables.Attach(null); }
    }

    [Fact]
    public void AModuleThatNamedNoPage_LeavesTheSpanInert()
    {
        var playables = new ModulePlayableCache(() => 0);
        ModulePlayables.Attach(playables);
        try
        {
            string uri = ModuleUri.Encode(ModuleId, "video:abc");
            playables.Put(uri, Resolved(pageEntityId: null, subtitleEntityId: null));

            Assert.Null(ModulePages.RouteFor(uri, LinkSlot.Title));
            Assert.Null(ModulePages.RouteFor(uri, LinkSlot.Artist));
            // …and a playable nobody has resolved is inert too, rather than guessing an entity id from the uri.
            Assert.Null(ModulePages.RouteFor(ModuleUri.Encode(ModuleId, "video:unresolved"), LinkSlot.Title));
        }
        finally { ModulePlayables.Attach(null); }
    }

    [Fact]
    public void ANonModuleUri_IsNeverAModulePage()
    {
        Assert.Null(ModulePages.RouteFor("spotify:track:1", LinkSlot.Title));
        Assert.Null(ModulePages.RouteFor((string?)null, LinkSlot.Title));
        Assert.Null(ModulePages.RouteFor((Track?)null, LinkSlot.Title));
    }

    internal static ResolvedPlayable Resolved(string? pageEntityId, string? subtitleEntityId)
        => new("video:abc", "Claude FM", ["Anthropic"], null, 0, true, Wavee.Sdk.MediaForm.Video,
               MediaLocator.FromUrl("https://example.com/master.m3u8", MediaLocator.ContainerHls),
               null, [], 0f, null, pageEntityId, subtitleEntityId);
}
