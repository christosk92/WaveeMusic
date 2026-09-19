using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public sealed class HomeLandingProjectionTests
{
    static HomeCard Card(string id, string? format = null) => new(
        "spotify:playlist:" + id, id, null, null, HomeCardKind.Playlist,
        Meta: new HomeCardMeta(Format: format));

    [Fact]
    public void SameKindGroups_BecomeOneOrderedUniqueLandingModule_AndLeaveNoDuplicateDeckTiles()
    {
        var one = Card("one");
        var duplicate = Card("same");
        var three = Card("three");
        var a = new HomeGroup(HomeGroupKind.QuickGrid, "First", [one, duplicate], Uri: "spotify:section:a", TotalCount: 8);
        var b = new HomeGroup(HomeGroupKind.QuickGrid, "Second", [duplicate, three], Uri: "spotify:section:b", TotalCount: 64);
        HomeSection[] sections =
        [
            new("spotify:section:a", "First", null, a.Cards, 8, 2),
            new("spotify:section:b", "Second", null, b.Cards, 64, 2),
        ];
        var feed = new HomeFeed("", [a, b], Sections: sections);

        var landing = HomeLandingProjection.Project(feed, HomeModuleTitles.Default);
        var quick = Assert.IsType<HomeLandingModule>(landing.Get(HomeGroupKind.QuickGrid));

        Assert.Equal([one.Uri, duplicate.Uri, three.Uri], quick.Group.Cards.Select(c => c.Uri));
        // Two differently labelled sections merged into ONE grid can honestly wear neither label, so the app's copy
        // stands. (A grid fed by a SINGLE labelled section keeps that label — see the composer-side pin.)
        Assert.Equal(HomeModuleTitles.Default.JumpBackIn, quick.Group.Title);
        Assert.Equal(64, quick.Group.TotalCount);
        Assert.Same(sections[1], quick.PrimarySection);
        Assert.Empty(landing.Sections);
        Assert.Same(sections, feed.Sections);
    }

    [Fact]
    public void Weekly_IsOneCanonicalPair_AndDuplicateFormatsStayOnlyInLedger()
    {
        var releaseA = Card("radar-a", "release-radar");
        var discover = Card("discover", "discover-weekly");
        var releaseB = Card("radar-b", "release-radar");
        var groups = new HomeGroup[]
        {
            new(HomeGroupKind.WeeklyPair, "Made for you", [releaseA, discover], Uri: "spotify:section:a"),
            new(HomeGroupKind.WeeklyPair, "Recently played", [releaseB], Uri: "spotify:section:b"),
        };
        var sections = groups.Select(g => new HomeSection(g.Uri, g.Title, null, g.Cards, g.Cards.Count, g.Cards.Count)).ToArray();

        var landing = HomeLandingProjection.Project(new HomeFeed("", groups, Sections: sections), HomeModuleTitles.Default);
        var weekly = Assert.IsType<HomeLandingModule>(landing.Get(HomeGroupKind.WeeklyPair));

        Assert.Equal(["discover-weekly", "release-radar"], weekly.Group.Cards.Select(c => c.Meta!.Format));
        var unconsumed = Assert.Single(landing.Sections);
        Assert.Same(sections[1], unconsumed);
        Assert.Contains(releaseB, unconsumed.Cards);
    }

    [Fact]
    public void WeeklySingleton_HasNoTwoUpRow_ButItsCardStillReachesTheLanding()
    {
        // The product decision has two halves, and only the first was ever pinned. Half of an authored 1fr 1fr
        // appointment row is a hole, so the two-up module stays SUPPRESSED — but the module is not the card. The
        // composer routes discover-weekly/release-radar EXCLUSIVELY to WeeklyPair, so a young account (Discover Weekly
        // arrives weeks before a first Release Radar) lost the card from the landing altogether. It now falls through
        // to the shapeless quick grid, which is where a card whose format names no module goes anyway.
        var release = Card("radar", "release-radar");
        var group = new HomeGroup(HomeGroupKind.WeeklyPair, "Recently played", [release], Uri: "spotify:section:radar");
        var section = new HomeSection(group.Uri, group.Title, null, group.Cards, 1, 1);

        var landing = HomeLandingProjection.Project(
            new HomeFeed("", [group], Sections: [section]), HomeModuleTitles.Default);

        Assert.Null(landing.Get(HomeGroupKind.WeeklyPair));
        var quick = Assert.IsType<HomeLandingModule>(landing.Get(HomeGroupKind.QuickGrid));
        Assert.Equal([release.Uri], quick.Group.Cards.Select(c => c.Uri));
        // Its section is represented by the fallback module, so the deck must not repeat it.
        Assert.Equal(HomeModuleTitles.Default.JumpBackIn, quick.Group.Title);
        Assert.Empty(landing.Sections);
    }

    [Fact]
    public void LoneWeeklyCard_LeadsTheQuickGrid_AndIsNeverDuplicatedIntoIt()
    {
        var discover = Card("discover", "discover-weekly");
        var one = Card("one");
        var lone = new HomeGroup(HomeGroupKind.WeeklyPair, null, [discover], Uri: "spotify:section:dw");

        // AHEAD of the picks: the grid renders only its first HomeModuleLayout.QuickShown cards on the landing, so
        // appending would rescue the card from the feed and hide it from the page in the same move.
        var landing = HomeLandingProjection.Project(
            new HomeFeed("", [lone, new HomeGroup(HomeGroupKind.QuickGrid, "Picks", [one])]), HomeModuleTitles.Default);
        Assert.Equal([discover.Uri, one.Uri],
            Assert.IsType<HomeLandingModule>(landing.Get(HomeGroupKind.QuickGrid)).Group.Cards.Select(c => c.Uri));

        // A grid that already holds the card (the same URI can be filed in two sections) is left exactly as it was.
        var already = HomeLandingProjection.Project(
            new HomeFeed("", [lone, new HomeGroup(HomeGroupKind.QuickGrid, "Picks", [one, discover])]),
            HomeModuleTitles.Default);
        Assert.Equal([one.Uri, discover.Uri],
            Assert.IsType<HomeLandingModule>(already.Get(HomeGroupKind.QuickGrid)).Group.Cards.Select(c => c.Uri));
    }

    [Fact]
    public void PendingSeed_ContainsTheSameCanonicalWeeklyPairAsReadyContent()
    {
        var landing = HomeLandingProjection.Project(FakeData.HomeSeed, HomeModuleTitles.Default);
        var weekly = Assert.IsType<HomeLandingModule>(landing.Get(HomeGroupKind.WeeklyPair));

        Assert.Equal(["discover-weekly", "release-radar"],
            weekly.Group.Cards.Select(c => c.Meta!.Format));
    }

    [Fact]
    public void Directory_KeepsEveryIdentifiedSectionInResponseOrder_AndDeduplicatesOnlyItsUri()
    {
        var a = new HomeSection("spotify:section:a", "A", null, [Card("one")], 1, 1);
        var b = new HomeSection("spotify:section:b", "B", null, [Card("two")], 1, 1);
        var duplicateA = new HomeSection("spotify:section:a", "A again", null, [Card("three")], 1, 1);
        var noUri = new HomeSection(null, "Local", null, [Card("four")], 1, 1);

        var landing = HomeLandingProjection.Project(
            new HomeFeed("", [], Sections: [a, b, duplicateA, noUri]), HomeModuleTitles.Default);

        Assert.Equal(["A", "B", "Local"], landing.Sections.Select(s => s.Title));
    }

    [Fact]
    public void Directory_ExcludesTheRecentsSourceAlreadyConsumedByItsTypedModule()
    {
        const string recentsUri = "spotify:list:recents:main";
        var recent = Card("recent");
        var recents = new HomeGroup(HomeGroupKind.Recents, "Recents", [recent], Uri: recentsUri);
        var recentsSection = new HomeSection(recentsUri, "Recents", null, [recent], 20, 20);
        var extra = new HomeSection("spotify:section:extra", "Extra", null, [Card("extra")], 1, 1);

        var landing = HomeLandingProjection.Project(
            new HomeFeed("", [recents], Sections: [recentsSection, extra]), HomeModuleTitles.Default);

        Assert.NotNull(landing.Get(HomeGroupKind.Recents));
        Assert.Same(extra, Assert.Single(landing.Sections));
    }
}
