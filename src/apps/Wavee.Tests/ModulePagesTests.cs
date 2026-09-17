// The module-PAGE cache, the route algebra, the identity-slot links and the page fetch — ported from 0.2.9
// Wavee.Tests/Modules/ModulePagesTests.cs onto `Modules.Pages` / `Modules.PlayableLinks` / `ModuleHost.PageAsync`, plus
// the `--fake` demo module's pages served over the in-process wire (the gate's fixture, ch 31).

using Wavee.Sdk;
using Xunit;
using static Wavee.Modules;

namespace Wavee.Tests;

[Collection(ModuleStaticsCollection.Name)]
public class ModulePagesTests
{
    const string ModuleId = "wavee.youtube";

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static ModulePageDoc Doc(long? expiresAt = null, string template = ModulePageDoc.TemplateEntity, PageAction[]? actions = null,
        params PageSection[] sections)
        => new(ModulePageDoc.CurrentVersion, template, new PageHero("Claude FM", "Channel", "Anthropic", null, "Live", true),
               actions ?? [], sections, expiresAt);

    // ── the cache ────────────────────────────────────────────────────────────────────────────────────────────────────

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

        now += 1;   // exactly AT the expiry instant — already gone
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
    public void ZeroExpiry_IsTreatedAsUnstated_NotAsAlreadyDead()
    {
        long now = 50_000;
        var cache = new ModulePageCache(() => now);
        string uri = ModuleUri.Encode(ModuleId, "video:zero");
        cache.Put(uri, Doc(expiresAt: 0));
        Assert.NotNull(cache.Get(uri));
    }

    [Fact]
    public void InvalidateDropsOnePage_InvalidateModuleDropsAllOfThem()
    {
        var cache = new ModulePageCache(() => 0);
        string a = ModuleUri.Encode(ModuleId, "video:a"), b = ModuleUri.Encode(ModuleId, "video:b"), other = ModuleUri.Encode("wavee.radio", "station:x");
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

    // ── the route algebra ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheRouteIsTheFamilyPrefixInFrontOfTheModuleUri()
    {
        string route = Pages.RouteForEntity(ModuleId, "channel:UC_x-y")!;
        Assert.Equal("module:" + ModuleUri.Encode(ModuleId, "channel:UC_x-y"), route);
        Assert.True(Pages.IsRoute(route));
    }

    [Fact]
    public void ARouteRoundTripsBackToItsTwoHalves()
    {
        const string entity = "station:https://stream.example.com/live?x=1";   // colons and slashes survive the payload
        string route = Pages.RouteForEntity(ModuleId, entity)!;

        Assert.True(Pages.TryParseRoute(route, out string moduleId, out string entityId));
        Assert.Equal(ModuleId, moduleId);
        Assert.Equal(entity, entityId);
        Assert.Equal(ModuleUri.Encode(ModuleId, entity), Pages.UriOf(route));
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
        Assert.False(Pages.TryParseRoute(key, out _, out _));
        if (key != "module:not-a-module-uri") Assert.Null(Pages.UriOf(key));
    }

    [Theory]
    [InlineData(null, "video:a")]
    [InlineData("", "video:a")]
    [InlineData(ModuleId, null)]
    [InlineData(ModuleId, "")]
    public void AHalfMissingLink_IsInertRatherThanADeadRoute(string? moduleId, string? entityId)
        => Assert.Null(Pages.RouteForEntity(moduleId, entityId));

    [Fact]
    public void TheShellParsesAModuleRouteIntoTheModuleKind_AndRoundTripsItsKey()
    {
        TestScope.Fresh();
        string key = Pages.RouteForEntity(ModuleId, "video:abc")!;
        Shell.Route route = Shell.Parse(key, "Claude FM");
        Assert.Equal(Shell.RouteKind.Module, route.Kind);
        Assert.Equal(key, Shell.NameOf(in route));
        Assert.Equal("Claude FM", Shell.ArgOf(in route));
    }

    // ── the three templates, the stage playable, the escape hatch ────────────────────────────────────────────────────

    [Fact]
    public void HeroFor_EntityAlwaysWearsOne_CustomOnlyWhenSupplied()
    {
        var bare = new ModulePageDoc(1, ModulePageDoc.TemplateEntity, null, [], [], null);
        Assert.Equal("From the card", Pages.HeroFor(bare, "  From the card ", "Module")!.Title);
        Assert.Equal("Module", Pages.HeroFor(bare, null, "Module")!.Title);
        Assert.Null(Pages.HeroFor(bare with { Template = ModulePageDoc.TemplateCustom }, "x", "Module"));
        Assert.Equal("Claude FM", Pages.HeroFor(Doc(template: ModulePageDoc.TemplateCustom), null, "Module")!.Title);
    }

    [Fact]
    public void StagePlayableUri_SpeaksThePlayableIdSpace()
    {
        var watch = Doc(template: ModulePageDoc.TemplateWatch, actions: [PageAction.Play("tRsQsTMvPNg", "Watch")]);
        Assert.Equal(ModuleUri.Encode(ModuleId, "tRsQsTMvPNg"), Pages.StagePlayableUri(ModuleId, watch));
        Assert.Equal("", Pages.StagePlayableUri(ModuleId, Doc(actions: [PageAction.Play("x", "Play")])));   // not a watch document
        Assert.True(Pages.IsPlayingEntity(ModuleUri.Encode(ModuleId, "a"), ModuleUri.Encode(ModuleId, "a")));
        Assert.False(Pages.IsPlayingEntity("", ""));
        Assert.False(Pages.IsPlayingEntity(ModuleUri.Encode(ModuleId, "a"), null));
    }

    [Fact]
    public void OpenUrlOf_TakesTheFirstWebUrl_AndRefusesEverythingElse()
    {
        Assert.Equal("https://youtu.be/1", Pages.OpenUrlOf(Doc(actions:
            [new PageAction("x", PageAction.KindOpenUrl, "Bad", null, "file:///C:/Windows/calc.exe", false),
             PageAction.OpenUrl("https://youtu.be/1", "Open"), PageAction.OpenUrl("https://youtu.be/2", "Open 2")])));
        Assert.Null(Pages.OpenUrlOf(Doc(actions: [PageAction.Play("x", "Play")])));
        Assert.Null(Pages.OpenUrlOf(null));
    }

    [Theory]
    [InlineData(10, 500, 10)]
    [InlineData(10, 4, 4)]
    [InlineData(10, 0, 0)]
    [InlineData(0, 500, 0)]
    public void TheSharedItemBudget_IsSpentInDocumentOrder(int count, int remaining, int expected)
        => Assert.Equal(expected, Pages.Take(count, remaining));

    // ── the identity-slot links (G-212) ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheIdentityClusterReadsTheModulesTwoEntityIds()
    {
        string uri = ModuleUri.Encode(ModuleId, "video:abc");
        ResolvedPlayable resolved = ModuleFixtures.Resolved("video:abc", pageEntityId: "video:abc", subtitleEntityId: "channel:UC1", artists: ["Anthropic"]);

        Assert.Equal(Pages.RouteForEntity(ModuleId, "video:abc"), PlayableLinks.RouteKeyFor(uri, Shell.LinkSlot.Title, resolved));
        Assert.Equal(Pages.RouteForEntity(ModuleId, "video:abc"), PlayableLinks.RouteKeyFor(uri, Shell.LinkSlot.Art, resolved));
        Assert.Equal(Pages.RouteForEntity(ModuleId, "channel:UC1"), PlayableLinks.RouteKeyFor(uri, Shell.LinkSlot.Artist, resolved));
        Assert.Equal("Anthropic", PlayableLinks.LabelFor(Shell.LinkSlot.Artist, resolved));
        Assert.Equal("A title", PlayableLinks.LabelFor(Shell.LinkSlot.Title, resolved));
    }

    [Fact]
    public void AModuleThatNamedNoPage_LeavesTheSpanInert()
    {
        string uri = ModuleUri.Encode(ModuleId, "video:abc");
        Assert.Null(PlayableLinks.RouteKeyFor(uri, Shell.LinkSlot.Title, ModuleFixtures.Resolved(pageEntityId: null)));
        Assert.Null(PlayableLinks.RouteKeyFor(uri, Shell.LinkSlot.Artist, ModuleFixtures.Resolved(subtitleEntityId: null)));
        Assert.Null(PlayableLinks.RouteKeyFor(uri, Shell.LinkSlot.Title, null));   // unresolved: never a guess
    }

    [Fact]
    public void ANonModuleUri_IsNeverAModulePage()
    {
        var resolved = ModuleFixtures.Resolved(pageEntityId: "video:abc");
        Assert.Null(PlayableLinks.RouteKeyFor("spotify:track:1", Shell.LinkSlot.Title, resolved));
        Assert.Null(PlayableLinks.RouteKeyFor(null, Shell.LinkSlot.Title, resolved));
    }

    // ── the page fetch ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PageAsync_FetchesOnce_ThenServesTheCache()
    {
        var script = new FakeModule { Page = p => Doc(sections: [PageSection.FromText("for " + p.EntityId)]) };
        (ModuleHost host, _) = ModuleFixtures.HostOver(script, ModuleFixtures.Manifest(capabilities: ["playback", "pages"]));
        using (host)
        {
            string uri = ModuleUri.Encode("wavee.fake", "video:1");
            ModulePageDoc first = await host.PageAsync(uri, Ct);
            ModulePageDoc second = await host.PageAsync(uri, Ct);

            Assert.Same(first, second);
            Assert.Equal(1, script.PageCalls);
            Assert.Equal("for video:1", first.Sections[0].Text);
        }
    }

    [Fact]
    public async Task PageAsync_AModuleWithoutThePagesCapability_IsNeverAsked()
    {
        var script = new FakeModule { Page = _ => Doc() };
        (ModuleHost host, Func<FakeModuleChannel?> channel) = ModuleFixtures.HostOver(script, ModuleFixtures.Manifest(capabilities: ["playback"]));
        using (host)
        {
            var ex = await Assert.ThrowsAsync<ModuleException>(() => host.PageAsync(ModuleUri.Encode("wavee.fake", "video:1"), Ct));
            Assert.Equal(ModuleErrorCode.Unsupported, ex.Code);
            Assert.Null(channel());   // never spawned to be told -32601
        }
    }

    [Fact]
    public async Task PageAsync_AnOverBudgetDocument_IsRefusedHostSide_AndAFailureIsNotLatched()
    {
        bool overBudget = true;
        var script = new FakeModule
        {
            Page = _ => overBudget
                ? Doc(sections: Enumerable.Range(0, ModulePageBudget.MaxSections + 1).Select(i => PageSection.FromText("s" + i)).ToArray())
                : Doc(),
        };
        (ModuleHost host, _) = ModuleFixtures.HostOver(script, ModuleFixtures.Manifest(capabilities: ["playback", "pages"]));
        using (host)
        {
            string uri = ModuleUri.Encode("wavee.fake", "video:1");
            var ex = await Assert.ThrowsAsync<ModuleException>(() => host.PageAsync(uri, Ct));
            Assert.Equal(ModuleErrorCode.Unsupported, ex.Code);

            overBudget = false;   // Retry reaches the module again
            Assert.NotNull(await host.PageAsync(uri, Ct));
            Assert.Equal(2, script.PageCalls);
        }
    }

    [Fact]
    public async Task TheDemoModule_ServesEveryTemplate_OverTheInProcessWire()
    {
        using var host = new ModuleHost(ModuleCatalog.From([DemoModule.Installed]), spawn: DemoModule.SpawnAsync, startIdleTimer: false);

        ModulePageDoc watch = await host.PageAsync(ModuleUri.Encode(DemoModule.Id, DemoModule.WatchEntity), Ct);
        WatchPageModel? model = WatchPageModel.From(watch, isPlayingEntity: false);
        Assert.NotNull(model);
        Assert.Equal("Wavee Demo Channel", model.ChannelName);
        Assert.Equal(DemoModule.ChannelEntity, model.ChannelEntityId);
        Assert.Equal(3, model.Shelf.Length);
        Assert.NotNull(model.FactLine);
        Assert.Equal(ModuleUri.Encode(DemoModule.Id, "demo-watch"), Pages.StagePlayableUri(DemoModule.Id, watch));

        ModulePageDoc station = await host.PageAsync(ModuleUri.Encode(DemoModule.Id, DemoModule.StationEntity), Ct);
        Assert.Null(WatchPageModel.From(station, false));
        Assert.Equal(5, station.Sections.Length);

        ModulePageDoc custom = await host.PageAsync(ModuleUri.Encode(DemoModule.Id, DemoModule.CustomEntity), Ct);
        Assert.Null(Pages.HeroFor(custom, null, "Module"));

        ModulePageDoc minimal = await host.PageAsync(ModuleUri.Encode(DemoModule.Id, DemoModule.MinimalEntity), Ct);
        Assert.Equal("Module", Pages.HeroFor(minimal, null, "Module")!.Title);

        var broken = await Assert.ThrowsAsync<ModuleException>(() => host.PageAsync(ModuleUri.Encode(DemoModule.Id, DemoModule.BrokenEntity), Ct));
        Assert.Equal(ModuleErrorCode.Unavailable, broken.Code);

        ModuleMatch? match = await host.MatchAsync("https://" + DemoModule.LinkHost + "/anything", null, Ct);
        Assert.NotNull(match);
        Assert.Equal(DemoModule.StationEntity, match.Resolved.PageEntityId);
        Assert.Equal(AudioShape.ModuleStream, AudioShapeOf(match.Resolved));
        Assert.StartsWith("module:", DemoModule.RouteOf(DemoModule.WatchEntity), StringComparison.Ordinal);
    }
}
