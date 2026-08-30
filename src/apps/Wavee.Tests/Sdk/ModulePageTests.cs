using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Sdk;
using Wavee.Sdk.Protocol;
using Xunit;

namespace Wavee.Tests.Sdk;

/// <summary>
/// The module-page contract: the DTOs survive a round-trip through the source-generated context with camelCase
/// members, an unknown section kind keeps its payload verbatim in <see cref="PageSection.Extra"/>, the budget
/// REJECTS (never truncates) an over-sized page, and <c>module/page</c> travels the real wire.
/// </summary>
public class ModulePageTests
{
    private static ModulePageDoc SampleDoc() => new(
        ModulePageDoc.CurrentVersion,
        ModulePageDoc.TemplateEntity,
        new PageHero("Claude FM", "Live stream", "Anthropic", "https://i.ytimg.com/vi/x/maxresdefault.jpg",
            "Live · 1,234 watching", IsLive: true),
        [
            PageAction.Play("tRsQsTMvPNg", "Play"),
            PageAction.OpenUrl("https://www.youtube.com/watch?v=tRsQsTMvPNg", "Open on YouTube"),
        ],
        [
            PageSection.FromFacts([["Views", "1,234"]], "About"),
            PageSection.FromText("A continuous broadcast.", "Description"),
            PageSection.FromPlayables(
            [
                new PageItem("Live now", "Anthropic", "https://i.ytimg.com/vi/x/hq.jpg", "tRsQsTMvPNg",
                    "video:tRsQsTMvPNg", null, MediaForm.Video, IsLive: true, "12:34"),
            ], "Now"),
        ],
        ExpiresAtUnixMs: 1767225600000L);

    // ---- round-trip ----------------------------------------------------------------------------------------------

    [Fact]
    public void Doc_RoundTripsThroughTheSourceGeneratedContext()
    {
        ModulePageDoc doc = SampleDoc();

        string json = JsonSerializer.Serialize(doc, SdkJsonContext.Default.ModulePageDoc);
        ModulePageDoc? back = JsonSerializer.Deserialize(json, SdkJsonContext.Default.ModulePageDoc);

        Assert.NotNull(back);
        Assert.Equal(doc.Version, back!.Version);
        Assert.Equal(doc.Template, back.Template);
        Assert.Equal(doc.ExpiresAtUnixMs, back.ExpiresAtUnixMs);
        Assert.Equal(doc.Hero, back.Hero);
        Assert.Equal(doc.Actions, back.Actions);
        Assert.Equal(doc.Sections.Length, back.Sections.Length);
        Assert.Equal(doc.Sections[1], back.Sections[1]);                    // text section: no arrays inside
        Assert.Equal(doc.Sections[0].Rows![0], back.Sections[0].Rows![0]);
        Assert.Equal(doc.Sections[2].Items![0], back.Sections[2].Items![0]);
    }

    [Fact]
    public void Doc_SerializesCamelCaseAndOmitsNulls()
    {
        string json = JsonSerializer.Serialize(SampleDoc(), SdkJsonContext.Default.ModulePageDoc);

        Assert.Contains("\"template\":\"entity\"", json, StringComparison.Ordinal);
        Assert.Contains("\"isLive\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"expiresAtUnixMs\":1767225600000", json, StringComparison.Ordinal);
        Assert.Contains("\"form\":\"video\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"facts\"", json, StringComparison.Ordinal);
        // WhenWritingNull is on: an absent member never ships as an explicit null.
        Assert.DoesNotContain(":null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvedPlayable_CarriesThePageAndSubtitleEntities()
    {
        var resolved = new ResolvedPlayable("tRsQsTMvPNg", "Claude FM", ["Anthropic"], null, 0, true,
            MediaForm.Video, MediaLocator.FromUrl("https://example/x.m3u8", MediaLocator.ContainerHls), null, [],
            PageEntityId: "video:tRsQsTMvPNg", SubtitleEntityId: "channel:UC123");

        string json = JsonSerializer.Serialize(resolved, SdkJsonContext.Default.ResolvedPlayable);
        ResolvedPlayable? back = JsonSerializer.Deserialize(json, SdkJsonContext.Default.ResolvedPlayable);

        Assert.Contains("\"pageEntityId\":\"video:tRsQsTMvPNg\"", json, StringComparison.Ordinal);
        Assert.Equal("channel:UC123", back!.SubtitleEntityId);
    }

    [Fact]
    public void ResolvedPlayable_WithoutPageEntities_StaysExactlyAsItWasBefore()
    {
        // The two members are additive with null defaults: an existing module that never sets them writes the same
        // bytes it wrote before this pass, so an older host reading a newer module sees no change at all.
        var resolved = new ResolvedPlayable("42", "Demo", [], null, 0, true, MediaForm.Audio,
            MediaLocator.FromUrl("https://example/s.mp3", MediaLocator.ContainerIcy), null, []);

        string json = JsonSerializer.Serialize(resolved, SdkJsonContext.Default.ResolvedPlayable);

        Assert.DoesNotContain("pageEntityId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("subtitleEntityId", json, StringComparison.Ordinal);
        Assert.Null(resolved.PageEntityId);
        Assert.Null(resolved.SubtitleEntityId);
    }

    // ---- the watch page ------------------------------------------------------------------------------------------

    [Fact]
    public void Hero_CarriesTheOwnerAvatarAndTheSubtitleEntity()
    {
        // The two pictures a watch page shows AT ONCE: ImageUrl is the entity's own artwork (a video thumbnail, which
        // becomes the stage's poster) and AvatarUrl is the OWNER's face (the circle beside the channel name). One
        // field could never have served both, which is why this is a real member rather than a convention.
        var hero = new PageHero("Claude FM", "Live stream", "Anthropic",
            "https://i.ytimg.com/vi/x/maxresdefault.jpg", "Live · 1,234 watching", IsLive: true,
            AvatarUrl: "https://yt3.ggpht.com/avatar=s176", SubtitleEntityId: "channel:UC123");

        string json = JsonSerializer.Serialize(hero, SdkJsonContext.Default.PageHero);
        PageHero? back = JsonSerializer.Deserialize(json, SdkJsonContext.Default.PageHero);

        Assert.Contains("\"avatarUrl\":\"https://yt3.ggpht.com/avatar=s176\"", json, StringComparison.Ordinal);
        Assert.Contains("\"subtitleEntityId\":\"channel:UC123\"", json, StringComparison.Ordinal);
        Assert.Equal(hero, back);
    }

    [Fact]
    public void Hero_WithoutTheWatchFields_StaysExactlyAsItWasBefore()
    {
        // Both members are additive with null defaults, so a module built against the older SDK writes the very bytes
        // it wrote before this pass — the same guarantee ResolvedPlayable's entity ids carry above. That is what lets
        // the app ship the watch layout before every module knows about it.
        var hero = new PageHero("Station", "Radio station", "Jazz", null, "128 kbps · MP3", IsLive: false);

        string json = JsonSerializer.Serialize(hero, SdkJsonContext.Default.PageHero);

        Assert.DoesNotContain("avatarUrl", json, StringComparison.Ordinal);
        Assert.DoesNotContain("subtitleEntityId", json, StringComparison.Ordinal);
        Assert.Null(hero.AvatarUrl);
        Assert.Null(hero.SubtitleEntityId);
    }

    [Fact]
    public void WatchTemplate_IsJustAnotherTemplateString_AndSurvivesTheWire()
    {
        // Template is a plain string switched by the renderer, so "watch" is a REQUEST for the video-first layout and
        // never a hard requirement: an app that does not know the value falls back to the entity layout and still
        // draws every section. Nothing about the document's shape changes with it.
        ModulePageDoc doc = SampleDoc() with { Template = ModulePageDoc.TemplateWatch };

        string json = JsonSerializer.Serialize(doc, SdkJsonContext.Default.ModulePageDoc);
        ModulePageDoc? back = JsonSerializer.Deserialize(json, SdkJsonContext.Default.ModulePageDoc);

        Assert.Contains("\"template\":\"watch\"", json, StringComparison.Ordinal);
        Assert.Equal(ModulePageDoc.TemplateWatch, back!.Template);
        Assert.Equal(doc.Sections.Length, back.Sections.Length);
        ModulePageBudget.Validate(back);   // a watch document is bound by exactly the same budgets
    }

    // ---- unknown kinds -------------------------------------------------------------------------------------------

    [Fact]
    public void UnknownSectionKind_KeepsItsPayloadInExtra()
    {
        const string json = """
        {"version":1,"template":"entity","actions":[],"sections":[
          {"kind":"timeline","title":"Schedule","entries":[{"at":"09:00","what":"Show"}],"density":3}
        ]}
        """;

        ModulePageDoc? doc = JsonSerializer.Deserialize(json, SdkJsonContext.Default.ModulePageDoc);

        Assert.NotNull(doc);
        PageSection section = Assert.Single(doc!.Sections);
        Assert.Equal("timeline", section.Kind);
        Assert.Equal("Schedule", section.Title);
        Assert.NotNull(section.Extra);
        Assert.True(section.Extra!.ContainsKey("entries"));
        Assert.Equal(3, section.Extra["density"].GetInt32());
        Assert.Equal("09:00", section.Extra["entries"][0].GetProperty("at").GetString());
    }

    [Fact]
    public void UnknownSectionKind_SurvivesARoundTripVerbatim()
    {
        var section = new PageSection("timeline", "Schedule", null, null, null,
            new Dictionary<string, JsonElement>
            {
                ["density"] = JsonDocument.Parse("3").RootElement.Clone(),
            });

        string json = JsonSerializer.Serialize(section, SdkJsonContext.Default.PageSection);
        PageSection? back = JsonSerializer.Deserialize(json, SdkJsonContext.Default.PageSection);

        // Extension data is FLATTENED, not nested under an "extra" member.
        Assert.Contains("\"density\":3", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"extra\"", json, StringComparison.Ordinal);
        Assert.Equal(3, back!.Extra!["density"].GetInt32());
    }

    // ---- budget --------------------------------------------------------------------------------------------------

    [Fact]
    public void Budget_AcceptsANormalPage() => ModulePageBudget.Validate(SampleDoc());

    [Fact]
    public void Budget_RejectsTooManySections()
    {
        var sections = new PageSection[ModulePageBudget.MaxSections + 1];
        for (int i = 0; i < sections.Length; i++) sections[i] = PageSection.FromText("x");
        var doc = new ModulePageDoc(1, ModulePageDoc.TemplateCustom, null, [], sections, null);

        ModuleException error = Assert.Throws<ModuleException>(() => ModulePageBudget.Validate(doc));

        Assert.Equal(ModuleErrorCode.Unsupported, error.Code);
        Assert.Contains("sections", error.Message, StringComparison.Ordinal);
        // Never truncates: the caller's document is untouched.
        Assert.Equal(ModulePageBudget.MaxSections + 1, doc.Sections.Length);
    }

    [Fact]
    public void Budget_RejectsTooManyItems()
    {
        // 20 sections x 30 items = 600 > 500, while staying under the section-count and byte limits.
        var sections = new PageSection[20];
        for (int i = 0; i < sections.Length; i++)
        {
            var items = new PageItem[30];
            for (int j = 0; j < items.Length; j++)
            {
                items[j] = new PageItem($"t{j}", null, null, $"p{j}", null, null, null, false, null);
            }

            sections[i] = PageSection.FromPlayables(items);
        }

        var doc = new ModulePageDoc(1, ModulePageDoc.TemplateCustom, null, [], sections, null);

        ModuleException error = Assert.Throws<ModuleException>(() => ModulePageBudget.Validate(doc));

        Assert.Equal(ModuleErrorCode.Unsupported, error.Code);
        Assert.Contains("items", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Budget_CountsFactRowsAsItemsToo()
    {
        var rows = new string[ModulePageBudget.MaxItems + 1][];
        for (int i = 0; i < rows.Length; i++) rows[i] = ["label", "value"];
        var doc = new ModulePageDoc(1, ModulePageDoc.TemplateCustom, null, [],
            [PageSection.FromFacts(rows)], null);

        ModuleException error = Assert.Throws<ModuleException>(() => ModulePageBudget.Validate(doc));

        Assert.Equal(ModuleErrorCode.Unsupported, error.Code);
    }

    [Fact]
    public void Budget_RejectsAnOversizedSection()
    {
        var doc = new ModulePageDoc(1, ModulePageDoc.TemplateCustom, null, [],
            [PageSection.FromText(new string('x', ModulePageBudget.MaxSectionBytes + 16))], null);

        ModuleException error = Assert.Throws<ModuleException>(() => ModulePageBudget.Validate(doc));

        Assert.Equal(ModuleErrorCode.Unsupported, error.Code);
        Assert.Contains("bytes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Budget_RejectsAnOversizedDocument()
    {
        // Every section is legal on its own; together they blow the 2 MiB document ceiling.
        const int per = 60 * 1024;
        int count = (ModulePageBudget.MaxDocBytes / per) + 2;
        var sections = new PageSection[count];
        for (int i = 0; i < count; i++) sections[i] = PageSection.FromText(new string('y', per));
        var doc = new ModulePageDoc(1, ModulePageDoc.TemplateCustom, null, [], sections, null);

        ModuleException error = Assert.Throws<ModuleException>(() => ModulePageBudget.Validate(doc));

        Assert.Equal(ModuleErrorCode.Unsupported, error.Code);
        Assert.True(count <= ModulePageBudget.MaxSections, "the fixture must not trip the section-count limit first");
    }

    // ---- the wire ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Page_TravelsOverTheRealWire()
    {
        await using var rig = await PageRig.StartAsync(new PageModule());
        await rig.InitializeAsync();

        ModulePageDoc doc = await rig.Host.RequestAsync(ModuleMethods.Page, new PageParams("video:tRsQsTMvPNg"),
            SdkJsonContext.Default.PageParams, SdkJsonContext.Default.ModulePageDoc,
            TestContext.Current.CancellationToken);

        Assert.Equal(ModulePageDoc.TemplateEntity, doc.Template);
        Assert.Equal("Claude FM", doc.Hero!.Title);
        Assert.True(doc.Hero.IsLive);
        Assert.Equal(PageAction.KindPlay, doc.Actions[0].Kind);
        Assert.Equal(3, doc.Sections.Length);
    }

    [Fact]
    public async Task Page_ForAnUnknownEntity_AnswersNull()
    {
        await using var rig = await PageRig.StartAsync(new PageModule());
        await rig.InitializeAsync();

        ModulePageDoc? doc = await rig.Host.RequestAsync(ModuleMethods.Page, new PageParams("nope:1"),
            SdkJsonContext.Default.PageParams, SdkJsonContext.Default.ModulePageDoc,
            TestContext.Current.CancellationToken);

        Assert.Null(doc);
    }

    [Fact]
    public async Task Page_OverBudget_IsATypedErrorInsteadOfATruncatedPage()
    {
        await using var rig = await PageRig.StartAsync(new PageModule());
        await rig.InitializeAsync();

        ModuleException error = await Assert.ThrowsAsync<ModuleException>(async () =>
            await rig.Host.RequestAsync(ModuleMethods.Page, new PageParams("huge:1"),
                SdkJsonContext.Default.PageParams, SdkJsonContext.Default.ModulePageDoc,
                TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.Unsupported, error.Code);
    }

    [Fact]
    public async Task TestHost_PageAsync_DrivesTheModuleDirectly()
    {
        var module = new PageModule();
        var host = new ModuleTestHost(module);

        ModulePageDoc? doc = await host.PageAsync("video:tRsQsTMvPNg", TestContext.Current.CancellationToken);

        Assert.NotNull(doc);
        Assert.Equal("Claude FM", doc!.Hero!.Title);
        Assert.Null(await host.PageAsync("nope:1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TestHost_PageAsync_AppliesTheSameBudget()
    {
        var host = new ModuleTestHost(new PageModule());

        ModuleException error = await Assert.ThrowsAsync<ModuleException>(
            async () => await host.PageAsync("huge:1", TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.Unsupported, error.Code);
    }

    [Fact]
    public async Task Module_WithoutPages_AnswersNullByDefault()
    {
        var host = new ModuleTestHost(new PagelessModule());

        Assert.Null(await host.PageAsync("video:x", TestContext.Current.CancellationToken));
    }

    // ---- fakes ---------------------------------------------------------------------------------------------------

    /// <summary>A module that serves one page, plus a deliberately over-budget one.</summary>
    private sealed class PageModule : WaveeModule
    {
        public override ValueTask<ResolvedPlayable> ResolveAsync(string playableId, CancellationToken ct)
            => new(new ResolvedPlayable(playableId, "Claude FM", ["Anthropic"], null, 0, true, MediaForm.Video,
                MediaLocator.FromUrl("https://example/x.m3u8", MediaLocator.ContainerHls), null, [],
                PageEntityId: "video:" + playableId, SubtitleEntityId: "channel:UC123"));

        public override ValueTask<ModulePageDoc?> GetPageAsync(string entityId, CancellationToken ct)
        {
            if (entityId == "huge:1")
            {
                var sections = new PageSection[ModulePageBudget.MaxSections + 5];
                for (int i = 0; i < sections.Length; i++) sections[i] = PageSection.FromText("x");
                return new ValueTask<ModulePageDoc?>(
                    new ModulePageDoc(1, ModulePageDoc.TemplateCustom, null, [], sections, null));
            }

            if (!entityId.StartsWith("video:", StringComparison.Ordinal))
            {
                return new ValueTask<ModulePageDoc?>((ModulePageDoc?)null);
            }

            return new ValueTask<ModulePageDoc?>(SampleDoc());
        }
    }

    /// <summary>A module that never overrides <see cref="WaveeModule.GetPageAsync"/>.</summary>
    private sealed class PagelessModule : WaveeModule
    {
        public override ValueTask<ResolvedPlayable> ResolveAsync(string playableId, CancellationToken ct)
            => new(new ResolvedPlayable(playableId, "x", [], null, 0, false, MediaForm.Audio,
                MediaLocator.FromUrl("https://example/x.mp3"), null, []));
    }

    /// <summary>The module under <see cref="ModuleRunner"/> plus a host-side connection, over in-memory pipes.</summary>
    private sealed class PageRig : IAsyncDisposable
    {
        private readonly MemoryPipe _hostToModule = new();
        private readonly MemoryPipe _moduleToHost = new();
        private readonly CancellationTokenSource _cts = new();

        private Task _hostLoop = Task.CompletedTask;

        private PageRig(WaveeModule module)
        {
            Host = new JsonRpcConnection(_moduleToHost, _hostToModule);
            Runner = ModuleRunner.RunAsync(module, _hostToModule, _moduleToHost, _cts.Token);
        }

        public JsonRpcConnection Host { get; }

        public Task<int> Runner { get; }

        public static Task<PageRig> StartAsync(WaveeModule module)
        {
            var rig = new PageRig(module);
            rig._hostLoop = rig.Host.RunAsync(rig._cts.Token);
            return Task.FromResult(rig);
        }

        public Task<InitializeResult> InitializeAsync()
            => Host.RequestAsync(ModuleMethods.Initialize,
                new InitializeParams("test-host", ModuleProtocol.MinSupported, ModuleProtocol.Version,
                    "C:\\wavee-test", "en-US", 0, null),
                SdkJsonContext.Default.InitializeParams, SdkJsonContext.Default.InitializeResult, _cts.Token);

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _hostToModule.CompleteWriting();
            _moduleToHost.CompleteWriting();
            await Host.DisposeAsync();
            try
            {
                await Task.WhenAll(_hostLoop, Runner).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // teardown races are not test failures
            }

            _cts.Dispose();
        }
    }
}
