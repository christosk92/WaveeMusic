using System;
using Wavee;
using Wavee.Sdk;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The WATCH page's projection (<see cref="WatchPageModel"/>) — every layout DECISION a YouTube-shaped module page
/// makes, asserted as VALUES. The projection is pure (System + Wavee.Sdk, no FluentGpu type) precisely so these can
/// be behavioural assertions rather than a scan of the view's source text: which section becomes the fact line, which
/// becomes the shelf, and where the channel identity comes from are answers, not spellings.
/// </summary>
public class WatchPageModelTests
{
    // ── builders ──────────────────────────────────────────────────────────────────────────────────────────────────

    static ModulePageDoc Doc(string template, PageHero? hero = null, PageAction[]? actions = null,
                             params PageSection[] sections)
        => new(ModulePageDoc.CurrentVersion, template, hero, actions ?? [], sections, null);

    static PageHero Hero(string title = "The Video", string? subtitle = "Some Channel", string? avatar = null,
                         string? subtitleEntity = null, string? image = "https://cdn/thumb.jpg",
                         string? meta = "1.2M views", bool live = false)
        => new(title, null, subtitle, image, meta, live, avatar, subtitleEntity);

    static PageItem Item(string title, string? subtitle = null, string? image = null, string? playable = null,
                         string? entity = null, string? meta = null, bool live = false)
        => new(title, subtitle, image, playable, entity, null, null, live, meta);

    // ── the template gate ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OnlyAWatchTemplateProjects()
    {
        Assert.NotNull(WatchPageModel.From(Doc(ModulePageDoc.TemplateWatch, Hero()), false));
        Assert.Null(WatchPageModel.From(Doc(ModulePageDoc.TemplateEntity, Hero()), false));
        Assert.Null(WatchPageModel.From(Doc(ModulePageDoc.TemplateCustom, Hero()), false));
        Assert.Null(WatchPageModel.From(Doc("Watch", Hero()), false));   // ORDINAL: the value is a wire constant
        Assert.Null(WatchPageModel.From(Doc("", Hero()), false));
        Assert.Null(WatchPageModel.From(null, false));
    }

    // ── the STAGED identity: one id space, and it is the playable's ───────────────────────────────────────────────
    // This is the case 5454 green tests missed and the running app caught. A module's entity ids and its playable ids
    // are different namespaces on purpose, and everything the stage is arbitrated against (CurrentTrack.Uri) speaks
    // the PLAYABLE one. Taking the page's own entity uri instead made the two terms permanently unequal, so on the one
    // module the feature exists for the stage stayed a poster while its own video played.

    const string Module = "wavee.youtube";

    [Fact]
    public void TheStagedIdentityIsThePlayable_NotThePageEntity()
    {
        // YouTube's shape, verbatim: the PAGE is entity `video:tRsQsTMvPNg`; the thing that PLAYS is `tRsQsTMvPNg`.
        const string videoId = "tRsQsTMvPNg";
        const string entityId = "video:" + videoId;

        var doc = Doc(ModulePageDoc.TemplateWatch, Hero(), [PageAction.Play(videoId, "Watch")]);
        Assert.Equal(videoId, WatchPageModel.StagePlayableIdOf(doc));

        string staged = ModuleUri.Encode(Module, WatchPageModel.StagePlayableIdOf(doc)!);   // what ModulePage writes
        string playing = ModuleUri.Encode(Module, videoId);                                 // what CurrentTrack carries
        string pageUri = ModuleUri.Encode(Module, entityId);                                // what the ROUTE carries

        // The premise: these really are two different strings, so which one you pick is not a matter of taste.
        Assert.NotEqual(pageUri, playing);

        Assert.True(DockedVideoHosting.PageStageHosts(staged, playing));
        Assert.False(DockedVideoHosting.PageStageHosts(pageUri, playing));   // the regression, pinned

        Assert.True(DockedVideoHosting.ShouldMount(DockedVideoFace.PageStage, SurfacePlacement.Docked,
            staged, staged, playing));
        Assert.False(DockedVideoHosting.ShouldMount(DockedVideoFace.PageStage, SurfacePlacement.Docked,
            pageUri, pageUri, playing));

        // And the rail yields exactly when the stage takes it — the other half of the same one-surface invariant.
        Assert.Equal(DockedVideoHost.PageStage,
            DockedVideoHosting.HostFor(SurfacePlacement.Docked, staged, playing));
        Assert.Equal(DockedVideoHost.Rail,
            DockedVideoHosting.HostFor(SurfacePlacement.Docked, pageUri, playing));
    }

    [Fact]
    public void APageThatStagesNothingClaimsNothing()
    {
        Assert.Null(WatchPageModel.StagePlayableIdOf(null));
        // Not a watch document: an entity page has no stage to claim the surface for.
        Assert.Null(WatchPageModel.StagePlayableIdOf(
            Doc(ModulePageDoc.TemplateEntity, Hero(), [PageAction.Play("vid", "Play")])));
        // A watch document with no play action names no playable, so there is nothing to stage.
        Assert.Null(WatchPageModel.StagePlayableIdOf(
            Doc(ModulePageDoc.TemplateWatch, Hero(), [PageAction.OpenUrl("https://x/", "Open")])));
        Assert.Null(WatchPageModel.StagePlayableIdOf(
            Doc(ModulePageDoc.TemplateWatch, Hero(), [new PageAction("p", PageAction.KindPlay, "Play", "   ", null, true)])));
        Assert.Null(WatchPageModel.StagePlayableIdOf(Doc(ModulePageDoc.TemplateWatch, Hero())));

        // The FIRST play action wins, matching the stage CTA and the play capsule.
        Assert.Equal("first", WatchPageModel.StagePlayableIdOf(Doc(ModulePageDoc.TemplateWatch, Hero(),
            [PageAction.OpenUrl("https://x/", "Open"), PageAction.Play("first", "Watch"),
             PageAction.Play("second", "Also", primary: false, id: "play2")])));
    }

    // ── the stage ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheStageIsLiveOnlyWhenThisEntityIsPlaying()
    {
        Assert.Equal(WatchStageKind.Poster, WatchPageModel.From(Doc(ModulePageDoc.TemplateWatch, Hero()), false)!.Stage);
        Assert.Equal(WatchStageKind.Live, WatchPageModel.From(Doc(ModulePageDoc.TemplateWatch, Hero()), true)!.Stage);
    }

    [Fact]
    public void ThePosterIsTheEntitysOwnArtwork_NotTheOwnersAvatar()
    {
        var m = WatchPageModel.From(
            Doc(ModulePageDoc.TemplateWatch, Hero(image: "https://cdn/thumb.jpg", avatar: "https://cdn/avatar.jpg")),
            false)!;
        Assert.Equal("https://cdn/thumb.jpg", m.PosterUrl);
        Assert.Equal("https://cdn/avatar.jpg", m.ChannelAvatarUrl);
    }

    // ── facts dissolve into ONE line of values ────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheFirstFactsSection_BecomesAValueOnlyLine()
    {
        var m = WatchPageModel.From(Doc(ModulePageDoc.TemplateWatch, Hero(), null,
            PageSection.FromFacts([["Views", "1,234,567"], ["Published", "2 days ago"]]),
            PageSection.FromFacts([["Ignored", "second facts block"]])), false)!;

        // The LABELS are gone: they existed to caption a grey tile, and the tiles are what this layout deleted.
        Assert.Equal("1,234,567" + WatchPageModel.FactSeparator + "2 days ago", m.FactLine);
        Assert.DoesNotContain("Views", m.FactLine!, StringComparison.Ordinal);
        Assert.DoesNotContain("second facts block", m.FactLine!, StringComparison.Ordinal);
    }

    [Fact]
    public void AMalformedFactsRowIsSkipped_NotFatal()
    {
        var m = WatchPageModel.From(Doc(ModulePageDoc.TemplateWatch, Hero(), null,
            PageSection.FromFacts([["only-a-label"], ["Views", "   "], ["Length", "4:21"]])), false)!;
        Assert.Equal("4:21", m.FactLine);
    }

    [Fact]
    public void AFactsSectionWithNothingUsable_LeavesNoFactLine()
    {
        Assert.Null(WatchPageModel.From(Doc(ModulePageDoc.TemplateWatch, Hero(), null,
            PageSection.FromFacts([])), false)!.FactLine);
        Assert.Null(WatchPageModel.From(Doc(ModulePageDoc.TemplateWatch, Hero()), false)!.FactLine);
    }

    // ── the description card ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheFirstTextSection_IsTheDescription()
    {
        var m = WatchPageModel.From(Doc(ModulePageDoc.TemplateWatch, Hero(), null,
            PageSection.FromText("the real description"),
            PageSection.FromText("a second block nobody shows")), false)!;
        Assert.Equal("the real description", m.Description);
    }

    [Fact]
    public void ABlankTextSectionBuysNoCard()
        => Assert.Null(WatchPageModel.From(Doc(ModulePageDoc.TemplateWatch, Hero(), null,
            PageSection.FromText("   ")), false)!.Description);

    // ── the shelf ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PlayablesWinTheShelf_WhateverTheirOrder()
    {
        var m = WatchPageModel.From(Doc(ModulePageDoc.TemplateWatch, Hero(), null,
            PageSection.FromCards([Item("A card", entity: "channel:x")], "Cards"),
            PageSection.FromCards([Item("Another card", entity: "channel:y")], "More cards"),
            PageSection.FromPlayables([Item("Up next", playable: "video:2", meta: "4:21")], "Up next")), false)!;

        Assert.Equal("Up next", m.ShelfTitle);
        Assert.Single(m.Shelf);
        Assert.Equal("Up next", m.Shelf[0].Title);
        Assert.Equal("video:2", m.Shelf[0].PlayableId);
        Assert.Equal("4:21", m.Shelf[0].Meta);
    }

    [Fact]
    public void CardsAreTheFallbackShelf()
    {
        var m = WatchPageModel.From(Doc(ModulePageDoc.TemplateWatch, Hero(), null,
            PageSection.FromCards(
                [Item("Related one", subtitle: "Chan", image: "https://cdn/1.jpg", entity: "video:1"),
                 Item("Related two", entity: "video:2")], "Related")), false)!;

        Assert.Equal("Related", m.ShelfTitle);
        Assert.Equal(2, m.Shelf.Length);
        Assert.Equal("video:1", m.Shelf[0].EntityId);
        Assert.Equal("Chan", m.Shelf[0].Subtitle);
    }

    [Fact]
    public void AnEmptySectionIsNotTheShelf()
    {
        // The hero already carries its channel, so nothing here is claimed by the legacy one-card fallback.
        var m = WatchPageModel.From(Doc(ModulePageDoc.TemplateWatch, Hero(subtitleEntity: "channel:UC1"), null,
            PageSection.FromPlayables([], "Empty"),
            PageSection.FromCards([Item("A real card", entity: "video:9")], "Real")), false)!;
        Assert.Equal("Real", m.ShelfTitle);
        Assert.Single(m.Shelf);
    }

    [Fact]
    public void AnItemWithNoTitleIsDropped()
    {
        var m = WatchPageModel.From(Doc(ModulePageDoc.TemplateWatch, Hero(), null,
            PageSection.FromPlayables([Item("   "), Item("Real", playable: "video:3")])), false)!;
        Assert.Single(m.Shelf);
        Assert.Equal("Real", m.Shelf[0].Title);
    }

    // ── the channel row ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheHeroOwnsTheChannel_WhenItCarriesOne()
    {
        var m = WatchPageModel.From(Doc(ModulePageDoc.TemplateWatch,
            Hero(subtitle: "Some Channel", avatar: "https://cdn/avatar.jpg", subtitleEntity: "channel:UC1"), null,
            PageSection.FromCards([Item("Related", entity: "video:7")], "Related")), false)!;

        Assert.Equal("Some Channel", m.ChannelName);
        Assert.Equal("https://cdn/avatar.jpg", m.ChannelAvatarUrl);
        Assert.Equal("channel:UC1", m.ChannelEntityId);

        // The hero answered, so nothing was consumed: the cards section is still the shelf.
        Assert.Equal("Related", m.ShelfTitle);
        Assert.Single(m.Shelf);
    }

    /// <summary>An UN-UPDATED module: no <c>SubtitleEntityId</c>, because before it existed a page could only link
    /// onward to its channel through a one-card cards shelf. The projection reads that card as the channel — and then
    /// must NOT also hand it to the shelf, or the page shows the channel twice and calls the second one "related".</summary>
    [Fact]
    public void AOneCardShelf_IsTheLegacyChannelLink_AndIsNotAlsoTheShelf()
    {
        var m = WatchPageModel.From(Doc(ModulePageDoc.TemplateWatch,
            Hero(subtitle: "Some Channel", subtitleEntity: null), null,
            PageSection.FromCards([Item("Some Channel", image: "https://cdn/avatar.jpg", entity: "channel:UC1")],
                "Channel")), false)!;

        Assert.Equal("Some Channel", m.ChannelName);
        Assert.Equal("channel:UC1", m.ChannelEntityId);
        Assert.Equal("https://cdn/avatar.jpg", m.ChannelAvatarUrl);
        Assert.Empty(m.Shelf);
        Assert.Null(m.ShelfTitle);
    }

    [Fact]
    public void TheLegacyChannelCard_DoesNotStealARelatedShelf()
    {
        var m = WatchPageModel.From(Doc(ModulePageDoc.TemplateWatch,
            Hero(subtitle: "Some Channel", subtitleEntity: null), null,
            PageSection.FromCards([Item("Some Channel", entity: "channel:UC1")], "Channel"),
            PageSection.FromCards([Item("Related one", entity: "video:1"), Item("Related two", entity: "video:2")],
                "Related")), false)!;

        Assert.Equal("channel:UC1", m.ChannelEntityId);
        Assert.Equal("Related", m.ShelfTitle);
        Assert.Equal(2, m.Shelf.Length);
    }

    [Fact]
    public void AMultiCardShelfIsNeverPromotedToTheChannel()
    {
        var m = WatchPageModel.From(Doc(ModulePageDoc.TemplateWatch,
            Hero(subtitle: "Some Channel", subtitleEntity: null), null,
            PageSection.FromCards([Item("Related one", entity: "video:1"), Item("Related two", entity: "video:2")],
                "Related")), false)!;

        Assert.Equal("Some Channel", m.ChannelName);
        Assert.Null(m.ChannelEntityId);          // a name with nowhere to go — the row is inert, never a dead link
        Assert.Equal("Related", m.ShelfTitle);
        Assert.Equal(2, m.Shelf.Length);
    }

    [Fact]
    public void NoNameMeansNoChannelRowAtAll()
    {
        var m = WatchPageModel.From(Doc(ModulePageDoc.TemplateWatch,
            Hero(subtitle: null, subtitleEntity: null), null,
            PageSection.FromCards([Item("   ", entity: "channel:UC1")], "Channel")), false)!;

        Assert.Null(m.ChannelName);
        Assert.Null(m.ChannelEntityId);
        Assert.Null(m.ChannelAvatarUrl);
    }

    // ── the capsules ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ChipsAreTheDocumentsActions_InOrder_WithPrimaryPreserved()
    {
        var m = WatchPageModel.From(Doc(ModulePageDoc.TemplateWatch, Hero(),
            [PageAction.Play("video:1", "Watch"),
             PageAction.OpenUrl("https://youtu.be/1", "Open on YouTube"),
             new PageAction("subscribe", PageAction.KindModuleAction, "Subscribe", null, null, false)]), false)!;

        Assert.Equal(3, m.Chips.Length);
        Assert.Equal(new[] { "Watch", "Open on YouTube", "Subscribe" }, Array.ConvertAll(m.Chips, c => c.Label));
        Assert.True(m.Chips[0].Primary);
        Assert.False(m.Chips[1].Primary);
        Assert.Equal(PageAction.KindPlay, m.Chips[0].Kind);
        Assert.Equal("video:1", m.Chips[0].PlayableId);
        Assert.Equal("https://youtu.be/1", m.Chips[1].Url);
        Assert.Equal("subscribe", m.Chips[2].Id);
    }

    [Fact]
    public void AnUndrawableActionIsDropped()
    {
        var m = WatchPageModel.From(Doc(ModulePageDoc.TemplateWatch, Hero(),
            [new PageAction("a", PageAction.KindPlay, "   ", "video:1", null, false),
             new PageAction("b", "", "No kind", null, null, false),
             PageAction.Play("video:2", "Watch")]), false)!;

        Assert.Single(m.Chips);
        Assert.Equal("Watch", m.Chips[0].Label);
    }

    // ── garbage in ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AWatchDocumentWithNothingInItStillProjects()
    {
        var m = WatchPageModel.From(new ModulePageDoc(1, ModulePageDoc.TemplateWatch, null, null!, null!, null), false)!;
        Assert.NotNull(m);
        Assert.Equal("", m.Title);
        Assert.Null(m.MetaLine);
        Assert.False(m.IsLive);
        Assert.Null(m.ChannelName);
        Assert.Null(m.PosterUrl);
        Assert.Null(m.FactLine);
        Assert.Null(m.Description);
        Assert.Null(m.ShelfTitle);
        Assert.Empty(m.Chips);
        Assert.Empty(m.Shelf);
    }

    [Fact]
    public void ANullSectionInTheMiddleIsSkipped()
    {
        var m = WatchPageModel.From(Doc(ModulePageDoc.TemplateWatch, Hero(), null,
            null!, PageSection.FromText("still drawn")), false)!;
        Assert.Equal("still drawn", m.Description);
    }

    [Fact]
    public void WhitespacePaddingNeverBuysAField()
    {
        var m = WatchPageModel.From(Doc(ModulePageDoc.TemplateWatch,
            new PageHero("  Padded Title  ", null, "  ", "   ", "  ", true, "  ", "  ")), false)!;

        Assert.Equal("Padded Title", m.Title);
        Assert.Null(m.MetaLine);
        Assert.Null(m.PosterUrl);
        Assert.Null(m.ChannelName);
        Assert.Null(m.ChannelAvatarUrl);
        Assert.Null(m.ChannelEntityId);
        Assert.True(m.IsLive);
    }

    [Fact]
    public void TheLiveFlagAndMetaLineComeFromTheHero()
    {
        var m = WatchPageModel.From(Doc(ModulePageDoc.TemplateWatch,
            Hero(meta: "Live · 12,345 watching", live: true)), false)!;
        Assert.True(m.IsLive);
        Assert.Equal("Live · 12,345 watching", m.MetaLine);
    }
}
