// ── Wavee.Tests/HomeLandingProjectionTests.cs — the landing rhythm (Wave 5, owner P; ported from 0.2.9) ──────────────────
//
// The 0.2.9 facts, verbatim, over 0.3's card HANDLES: a card is committed through `HomeFixtures` (entity row + card fact),
// and the groups / section views are built around those handles exactly as the old records were. The one type map: a
// card's uri comparison became its entity identity (`DedupeKey`), so "the same card in two groups" is the same handle.

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public sealed class HomeLandingProjectionTests
{
    static HomeCard Card(string id, string? format = null)
        => HomeFixtures.Card(HomeFixtures.Playlist("spotify:playlist:" + id, id, format));

    static HomeSectionView Section(string? uri, string? title, IReadOnlyList<HomeCard> cards, int total, int raw)
        => new(Table.None, uri, title, null, cards, total, raw);

    [Fact]
    public void SameKindGroups_BecomeOneOrderedUniqueLandingModule_AndLeaveNoDuplicateDeckTiles()
    {
        TestScope.Fresh();
        var one = Card("one");
        var duplicate = Card("same");
        var three = Card("three");
        var a = new HomeGroup(HomeGroupKind.QuickGrid, "First", [one, duplicate], Uri: "spotify:section:a", TotalCount: 8);
        var b = new HomeGroup(HomeGroupKind.QuickGrid, "Second", [duplicate, three], Uri: "spotify:section:b", TotalCount: 64);
        HomeSectionView[] sections =
        [
            Section("spotify:section:a", "First", a.Cards, 8, 2),
            Section("spotify:section:b", "Second", b.Cards, 64, 2),
        ];
        var feed = new HomeFeedView("", [a, b], Sections: sections);

        var landing = HomeLandingProjection.Project(feed, HomeModuleTitles.Default);
        var quick = Assert.IsType<HomeLandingModule>(landing.Get(HomeGroupKind.QuickGrid));

        Assert.Equal(new[] { one.Uri, duplicate.Uri, three.Uri }, quick.Group.Cards.Select(c => c.Uri));
        // Two differently labelled sections merged into ONE grid can honestly wear neither label.
        Assert.Equal(HomeModuleTitles.Default.JumpBackIn, quick.Group.Title);
        Assert.Equal(64, quick.Group.TotalCount);
        Assert.Same(sections[1], quick.PrimarySection);
        Assert.Empty(landing.Sections);
        Assert.Same(sections, feed.Sections);
    }

    [Fact]
    public void Weekly_IsOneCanonicalPair_AndDuplicateFormatsStayOnlyInLedger()
    {
        TestScope.Fresh();
        var releaseA = Card("radar-a", "release-radar");
        var discover = Card("discover", "discover-weekly");
        var releaseB = Card("radar-b", "release-radar");
        var groups = new HomeGroup[]
        {
            new(HomeGroupKind.WeeklyPair, "Made for you", [releaseA, discover], Uri: "spotify:section:a"),
            new(HomeGroupKind.WeeklyPair, "Recently played", [releaseB], Uri: "spotify:section:b"),
        };
        var sections = groups.Select(g => Section(g.Uri, g.Title, g.Cards, g.Cards.Count, g.Cards.Count)).ToArray();

        var landing = HomeLandingProjection.Project(new HomeFeedView("", groups, Sections: sections), HomeModuleTitles.Default);
        var weekly = Assert.IsType<HomeLandingModule>(landing.Get(HomeGroupKind.WeeklyPair));

        Assert.Equal(new[] { "discover-weekly", "release-radar" }, weekly.Group.Cards.Select(c => c.Format));
        var unconsumed = Assert.Single(landing.Sections);
        Assert.Same(sections[1], unconsumed);
        Assert.Contains(releaseB, unconsumed.Cards);
    }

    [Fact]
    public void WeeklySingleton_HasNoTwoUpRow_ButItsCardStillReachesTheLanding()
    {
        // Half of an authored 1fr 1fr appointment row is a hole, so the two-up stays SUPPRESSED — but the module is not
        // the card: it falls through to the shapeless quick grid.
        TestScope.Fresh();
        var release = Card("radar", "release-radar");
        var group = new HomeGroup(HomeGroupKind.WeeklyPair, "Recently played", [release], Uri: "spotify:section:radar");
        var section = Section(group.Uri, group.Title, group.Cards, 1, 1);

        var landing = HomeLandingProjection.Project(new HomeFeedView("", [group], Sections: [section]), HomeModuleTitles.Default);

        Assert.Null(landing.Get(HomeGroupKind.WeeklyPair));
        var quick = Assert.IsType<HomeLandingModule>(landing.Get(HomeGroupKind.QuickGrid));
        Assert.Equal(new[] { release.Uri }, quick.Group.Cards.Select(c => c.Uri));
        Assert.Equal(HomeModuleTitles.Default.JumpBackIn, quick.Group.Title);
        Assert.Empty(landing.Sections);
    }

    [Fact]
    public void LoneWeeklyCard_LeadsTheQuickGrid_AndIsNeverDuplicatedIntoIt()
    {
        TestScope.Fresh();
        var discover = Card("discover", "discover-weekly");
        var one = Card("one");
        var lone = new HomeGroup(HomeGroupKind.WeeklyPair, null, [discover], Uri: "spotify:section:dw");

        // AHEAD of the picks: the grid renders only its first HomeModuleLayout.QuickShown cards.
        var landing = HomeLandingProjection.Project(
            new HomeFeedView("", [lone, new HomeGroup(HomeGroupKind.QuickGrid, "Picks", [one])]), HomeModuleTitles.Default);
        Assert.Equal(new[] { discover.Uri, one.Uri },
            Assert.IsType<HomeLandingModule>(landing.Get(HomeGroupKind.QuickGrid)).Group.Cards.Select(c => c.Uri));

        // A grid that already holds the card is left exactly as it was.
        var already = HomeLandingProjection.Project(
            new HomeFeedView("", [lone, new HomeGroup(HomeGroupKind.QuickGrid, "Picks", [one, discover])]),
            HomeModuleTitles.Default);
        Assert.Equal(new[] { one.Uri, discover.Uri },
            Assert.IsType<HomeLandingModule>(already.Get(HomeGroupKind.QuickGrid)).Group.Cards.Select(c => c.Uri));
    }

    [Fact]
    public void PendingSeed_ContainsTheSameCanonicalWeeklyPairAsReadyContent()
    {
        var landing = HomeLandingProjection.Project(HomeFeedView.Seed, HomeModuleTitles.Default);
        var weekly = Assert.IsType<HomeLandingModule>(landing.Get(HomeGroupKind.WeeklyPair));

        Assert.Equal(new[] { "discover-weekly", "release-radar" }, weekly.Group.Cards.Select(c => c.Format));
    }

    [Fact]
    public void Directory_KeepsEveryIdentifiedSectionInResponseOrder_AndDeduplicatesOnlyItsUri()
    {
        TestScope.Fresh();
        var a = Section("spotify:section:a", "A", [Card("one")], 1, 1);
        var b = Section("spotify:section:b", "B", [Card("two")], 1, 1);
        var duplicateA = Section("spotify:section:a", "A again", [Card("three")], 1, 1);
        var noUri = Section(null, "Local", [Card("four")], 1, 1);

        var landing = HomeLandingProjection.Project(
            new HomeFeedView("", [], Sections: [a, b, duplicateA, noUri]), HomeModuleTitles.Default);

        Assert.Equal(new[] { "A", "B", "Local" }, landing.Sections.Select(s => s.Title));
    }

    [Fact]
    public void Directory_ExcludesTheRecentsSourceAlreadyConsumedByItsTypedModule()
    {
        TestScope.Fresh();
        const string recentsUri = "spotify:list:recents:main";
        var recent = Card("recent");
        var recents = new HomeGroup(HomeGroupKind.Recents, "Recents", [recent], Uri: recentsUri);
        var recentsSection = Section(recentsUri, "Recents", [recent], 20, 20);
        var extra = Section("spotify:section:extra", "Extra", [Card("extra")], 1, 1);

        var landing = HomeLandingProjection.Project(
            new HomeFeedView("", [recents], Sections: [recentsSection, extra]), HomeModuleTitles.Default);

        Assert.NotNull(landing.Get(HomeGroupKind.Recents));
        Assert.Same(extra, Assert.Single(landing.Sections));
    }
}
